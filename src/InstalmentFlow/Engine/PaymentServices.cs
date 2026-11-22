using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

internal sealed partial class PaymentsService(TenantContext context) : IPayments
{
    private const int MaxMetadataEntries = 32;
    private const int MaxMetadataKeyLength = 64;
    private const int MaxMetadataValueLength = 512;

    private readonly CommandRunner _runner = new(context);

    public Task<RecordPaymentResult> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var source = Validate.Required(request.Source, nameof(request.Source));
        var amount = Validate.Required(request.Amount, nameof(request.Amount));
        var target = Validate.Required(request.Target, nameof(request.Target));
        if (!amount.IsPositive)
        {
            throw new InvalidAmountException($"Payments must be positive; got {amount}.");
        }

        var metadata = ValidateMetadata(request.Metadata);
        return RecordAndMeasureAsync();

        async Task<RecordPaymentResult> RecordAndMeasureAsync()
        {
            var result = await _runner.RunAsync(
                request,
                "RecordPayment",
                (_, _) => Task.FromResult(Fingerprint(request, metadata)),
                (tenant, ct) => PlanAsync(tenant, request, source, amount, target, metadata, ct),
                result => result with { Outcome = RecordOutcome.Duplicate },
                cancellationToken).ConfigureAwait(false);
            Telemetry.PaymentRecorded(result, source);
            return result;
        }
    }

    private async Task<Planned<RecordPaymentResult>> PlanAsync(
        TenantSnapshot tenant,
        RecordPaymentRequest request,
        ExternalReference source,
        Money amount,
        PaymentTarget target,
        ImmutableSortedDictionary<string, string> metadata,
        CancellationToken ct)
    {
        // Invariant I-3: one payment per provider reference. A repeat with a new idempotency key is a duplicate.
        var existing = await context.Store.FindPaymentAsync(context.TenantId, source, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Amount != amount)
            {
                throw new RuleViolationException(
                    ErrorCodes.PaymentConflict,
                    $"Payment {source} was recorded as {existing.Amount}; a notification now says {amount}. It was not recorded again.");
            }

            return new Planned<RecordPaymentResult>(CommitFor(), await DuplicateResultAsync(existing, ct).ConfigureAwait(false));
        }

        var resolved = await ResolveTargetAsync(tenant, target, ct).ConfigureAwait(false);
        var decision = PaymentIdentifier.Decide(resolved, amount.Currency, tenant.Tenant.BaseCurrency, tenant.Policy);

        var now = tenant.Clock.UtcNow;
        var paymentId = PaymentId.New(context.Time);
        var journalId = JournalTxId.New(context.Time);
        var lot = FundsLot.Received(paymentId, amount, decision.DirectedTo, request.ValueAt);
        var rawReference = Truncate(target.RawReference);

        var journal = new JournalBuilder(decision.HoldingAccount is null ? JournalKind.PaymentUnidentified : JournalKind.PaymentReceived)
            .Debit(LedgerAccounts.Clearing(source.Source), amount, LedgerSourceType.Payment, paymentId.Value)
            .Credit(
                decision.HoldingAccount is { } holder ? LedgerAccounts.Unapplied(holder, amount.Currency) : LedgerAccounts.Unidentified(source.Source),
                amount,
                LedgerSourceType.Payment,
                paymentId.Value);

        var events = ImmutableArray.CreateBuilder<DomainEvent>();
        events.Add(DomainEvent.Create(
            context.Time,
            decision.HoldingAccount is null ? "PaymentUnidentified" : "PaymentReceived",
            context.TenantId,
            decision.HoldingAccount,
            ("paymentId", paymentId.ToString()),
            ("source", source.ToString()),
            ("amount", amount.ToString())));

        var commit = CommitFor();
        var unappliedAfter = Money.Zero(amount.Currency);
        var allocation = (LotAllocation?)null;
        var exceptionReason = decision.Exception;
        var exceptionDetail = decision.Detail;
        var exceptionCandidates = decision.Candidates;

        // Reversal before payment: reversals parked for this provider reference are applied first (ADR-005).
        var pending = await context.Store.ListPendingReversalsAsync(context.TenantId, source, ct).ConfigureAwait(false);
        var reversals = ImmutableArray.CreateBuilder<PaymentReversalRecord>();
        var paymentRecordForReversal = new PaymentRecord(paymentId, source, amount, request.ValueAt, now, target.Kind, rawReference,
            decision.HoldingAccount, decision.DirectedTo, null, journalId, metadata);
        var mismatches = new List<string>();

        if (decision.HoldingAccount is { } accountId)
        {
            var account = (await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false)).AddLot(lot);
            foreach (var parked in pending)
            {
                if (ParkedAmount(parked, account.GetLot(paymentId), mismatches) is { } take)
                {
                    var applied = await Reversals.OnAccountAsync(context, tenant, account, paymentRecordForReversal, take, parked.Id, parked.Reference, parked.Reason, journalId, journal, ct).ConfigureAwait(false);
                    account = applied.Account!;
                    reversals.Add(applied.Record);
                    events.AddRange(applied.Events);
                }
            }

            var eligible = decision.DirectedTo is { } directed ? [directed] : decision.StrategyEligible;
            if (!account.GetLot(paymentId).Remaining.IsPositive)
            {
                eligible = [];
                exceptionReason = null;
            }

            if (!eligible.IsEmpty)
            {
                // ADR-002: a failing or faulty strategy must never cost the payment. Keep the funds and explain why.
                // Allocator.Run posts journal lines only after the plan is validated, so a failure leaves none behind.
                try
                {
                    allocation = Allocator.Run(context, tenant, account, paymentId, eligible, null, journalId, journal);
                    account = allocation.Account;
                    events.AddRange(Allocator.Events(context, allocation));
                }
#pragma warning disable CA1031 // Any strategy failure, including host code, must leave the payment recorded (ADR-002).
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    allocation = null;
                    exceptionReason = PaymentExceptionReason.AllocationFailed;
                    exceptionDetail = $"Allocation failed; the funds are held as unapplied. {ex.Message}";
                    exceptionCandidates = [];
                }
            }

            commit = commit with { Accounts = [account] };
            unappliedAfter = account.Unapplied(amount.Currency);
        }
        else
        {
            var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, source.Source, ct).ConfigureAwait(false);
            pool = pool with { Lots = pool.Lots.Add(lot) };
            foreach (var parked in pending)
            {
                if (ParkedAmount(parked, pool.GetLot(paymentId), mismatches) is { } take)
                {
                    var applied = Reversals.OnPool(context, tenant, pool, paymentRecordForReversal, take, parked.Id, parked.Reference, parked.Reason, journalId, journal);
                    pool = applied.Pool!;
                    reversals.Add(applied.Record);
                    events.AddRange(applied.Events);
                }
            }

            if (!pool.GetLot(paymentId).Remaining.IsPositive)
            {
                exceptionReason = null;
            }

            commit = commit with { Pools = [pool] };
        }

        if (mismatches.Count > 0)
        {
            exceptionReason = PaymentExceptionReason.ParkedReversalNotApplied;
            exceptionDetail = string.Join(" ", mismatches) + (decision.Exception is null ? string.Empty : $" Identification: {decision.Detail}");
            exceptionCandidates = decision.Candidates;
        }

        var exceptionId = exceptionReason is null ? (PaymentExceptionId?)null : PaymentExceptionId.New(context.Time);
        var payment = new PaymentRecord(
            paymentId, source, amount, request.ValueAt, now, target.Kind, rawReference, decision.HoldingAccount,
            decision.DirectedTo, exceptionId, journalId, metadata);

        PaymentExceptionView? exceptionView = null;
        if (exceptionId is { } id)
        {
            var exception = new PaymentExceptionRecord(id, paymentId, exceptionReason!.Value, exceptionDetail, rawReference, exceptionCandidates, now, null);
            commit = commit with { NewExceptions = [exception] };
            events.Add(DomainEvent.Create(context.Time, "PaymentExceptionRaised", context.TenantId, decision.HoldingAccount,
                ("exceptionId", id.ToString()), ("paymentId", paymentId.ToString()), ("reason", exception.Reason.ToString())));
            exceptionView = ViewMapper.ToView(exception, payment, decision.HoldingAccount);
        }

        var records = allocation?.Allocations ?? [];
        var allocated = Money.Sum(amount.Currency, records.Select(a => a.Amount));
        commit = commit with
        {
            Journal = journal.Build(context.TenantId, decision.HoldingAccount, request.IdempotencyKey, tenant.Clock.ToBusinessDate(request.ValueAt), now, context.Time, journalId),
            Payments = [payment],
            Allocations = records,
            PaymentReversals = reversals.ToImmutable(),
            AppliedPendingReversals = [.. pending.Select(p => p.Id)],
            Events = events.ToImmutable(),
        };

        var reversed = Money.Sum(amount.Currency, reversals.Select(r => r.Amount));
        var outcome = reversed == amount ? RecordOutcome.Reversed
            : decision.HoldingAccount is null ? RecordOutcome.Unidentified
            : allocated.IsPositive && allocated + reversed == amount ? RecordOutcome.Allocated
            : allocated.IsPositive ? RecordOutcome.PartiallyAllocated
            : RecordOutcome.HeldUnapplied;
        var result = new RecordPaymentResult(
            outcome,
            paymentId,
            decision.HoldingAccount,
            decision.DirectedTo,
            amount,
            allocated,
            unappliedAfter,
            [.. records.Select(Allocator.ToView)],
            allocation?.InstalmentsSettled ?? [],
            allocation?.ObligationsSettled ?? [],
            exceptionView);
        return new Planned<RecordPaymentResult>(commit, result);
    }

    /// <summary>
    /// How much of a parked reversal to apply to a just-recorded payment. Recording never fails (ADR-002), so a
    /// reversal in another currency, or for more than the payment, is not forced: it is applied as far as it can be
    /// and explained in a payment exception.
    /// </summary>
    private static Money? ParkedAmount(PendingReversal parked, FundsLot lot, List<string> mismatches)
    {
        if (parked.Amount is { } wanted && wanted.Currency != lot.Original.Currency)
        {
            mismatches.Add($"A reversal of {wanted} parked for this payment was not applied: the payment is in {lot.Original.Currency}.");
            return null;
        }

        var amount = Money.Min(parked.Amount ?? lot.Reversible, lot.Reversible);
        if (parked.Amount is { } requested && requested > amount)
        {
            mismatches.Add($"A reversal of {requested} parked for this payment was applied only up to {amount}.");
        }

        return amount.IsPositive ? amount : null;
    }

    private async Task<ResolvedTarget> ResolveTargetAsync(TenantSnapshot tenant, PaymentTarget target, CancellationToken ct)
    {
        switch (target.Kind)
        {
            case PaymentTargetKind.Obligation:
                var location = await TryResolveAsync(target.Obligation!, tenant, ct).ConfigureAwait(false);
                return location is { } found
                    ? new ResolvedTarget.OneObligation(await FactsAsync(found, ct).ConfigureAwait(false))
                    : new ResolvedTarget.NoMatch($"The obligation {target.Obligation} given with the payment was not found.");

            case PaymentTargetKind.Customer:
                var accountId = await TryResolveAsync(target.Account!, ct).ConfigureAwait(false);
                return accountId is { } customer
                    ? await CustomerAsync(customer, ct).ConfigureAwait(false)
                    : new ResolvedTarget.NoMatch($"The customer {target.Account} given with the payment was not found.");

            case PaymentTargetKind.Reference:
                return await ResolveReferenceAsync(tenant, target.RawReference ?? string.Empty, ct).ConfigureAwait(false);

            default:
                return new ResolvedTarget.NoMatch("No reference or customer was given with the payment.");
        }
    }

    private async Task<ResolvedTarget> ResolveReferenceAsync(TenantSnapshot tenant, string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResolvedTarget.NoMatch("The payment has no reference.");
        }

        if (raw.Length > PaymentTarget.MaxRawReferenceLength)
        {
            return new ResolvedTarget.NoMatch("The payment reference is too long to be valid.");
        }

        var obligations = new List<ObligationLocation>();
        var customers = new List<AccountId>();
        var valid = tenant.Policy.PaymentReference.TryNormalise(raw, out var normalised);
        ObligationLocation? exact = null;
        if (valid)
        {
            exact = await context.Store.FindObligationAsync(context.TenantId, new ObligationLookup(null, null, normalised), ct).ConfigureAwait(false);
            if (exact is { } match)
            {
                obligations.Add(match);
            }
        }

        if (context.Services.ReferenceResolver is { } resolver)
        {
            var extra = await resolver.ResolveAsync(new ReferenceResolutionContext(context.TenantId, raw, normalised, exact?.ObligationId), ct).ConfigureAwait(false);
            foreach (var candidate in extra ?? [])
            {
                if (candidate?.Obligation is { } obligationRef && await TryResolveAsync(obligationRef, tenant, ct).ConfigureAwait(false) is { } location)
                {
                    obligations.Add(location);
                }
                else if (candidate?.Account is { } accountRef && await TryResolveAsync(accountRef, ct).ConfigureAwait(false) is { } account)
                {
                    customers.Add(account);
                }
            }
        }

        var distinctObligations = obligations.Distinct().ToList();
        var distinctCustomers = customers.Distinct().ToList();
        var total = distinctObligations.Count + distinctCustomers.Count;

        if (total == 0)
        {
            return new ResolvedTarget.NoMatch(valid
                ? $"The reference '{normalised}' matches no obligation."
                : $"The reference '{raw}' does not match the tenant's format.");
        }

        if (total == 1)
        {
            return distinctObligations.Count == 1
                ? new ResolvedTarget.OneObligation(await FactsAsync(distinctObligations[0], ct).ConfigureAwait(false))
                : await CustomerAsync(distinctCustomers[0], ct).ConfigureAwait(false);
        }

        var views = ImmutableArray.CreateBuilder<CandidateView>(total);
        foreach (var location in distinctObligations)
        {
            var facts = await FactsAsync(location, ct).ConfigureAwait(false);
            views.Add(new CandidateView(facts.AccountId, facts.Id, facts.PaymentReference));
        }

        views.AddRange(distinctCustomers.Select(c => new CandidateView(c, null, null)));
        return new ResolvedTarget.Several(views.ToImmutable());
    }

    private async Task<ObligationFacts> FactsAsync(ObligationLocation location, CancellationToken ct)
    {
        var account = await context.LoadAccountAsync(location.AccountId, ct).ConfigureAwait(false);
        return ObligationFacts.From(account.GetObligation(location.ObligationId));
    }

    private async Task<ResolvedTarget> CustomerAsync(AccountId accountId, CancellationToken ct)
    {
        var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);
        return new ResolvedTarget.OneCustomer(accountId, [.. account.Obligations.Select(ObligationFacts.From)]);
    }

    private async Task<ObligationLocation?> TryResolveAsync(ObligationRef reference, TenantSnapshot tenant, CancellationToken ct)
    {
        try
        {
            return await context.ResolveAsync(reference, tenant, ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private async Task<AccountId?> TryResolveAsync(AccountRef reference, CancellationToken ct)
    {
        try
        {
            var id = await context.ResolveAsync(reference, ct).ConfigureAwait(false);
            return await context.Store.LoadAccountAsync(context.TenantId, id, ct).ConfigureAwait(false) is null ? null : id;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private async Task<RecordPaymentResult> DuplicateResultAsync(PaymentRecord existing, CancellationToken ct)
    {
        var holding = await PaymentLookup.HoldingAsync(context, existing, ct).ConfigureAwait(false);
        PaymentExceptionView? exception = null;
        if (existing.ExceptionId is { } exceptionId &&
            await context.Store.GetExceptionAsync(context.TenantId, exceptionId, ct).ConfigureAwait(false) is { } record)
        {
            exception = ViewMapper.ToView(record, existing, holding.AccountId);
        }

        var unapplied = holding.AccountId is { } holder
            ? (await context.LoadAccountAsync(holder, ct).ConfigureAwait(false)).Unapplied(existing.Amount.Currency)
            : Money.Zero(existing.Amount.Currency);
        var allocations = await context.Store.ListAllocationsAsync(context.TenantId, existing.Id, ct).ConfigureAwait(false);
        return new RecordPaymentResult(
            RecordOutcome.Duplicate, existing.Id, holding.AccountId, holding.Lot?.Directive, existing.Amount,
            Money.Sum(existing.Amount.Currency, allocations.Select(a => a.Amount)), unapplied,
            [.. allocations.Select(Allocator.ToView)], [], [], exception);
    }

    private Commit CommitFor() => new() { Tenant = context.TenantId };

    private static Fingerprint Fingerprint(RecordPaymentRequest request, ImmutableSortedDictionary<string, string> metadata)
    {
        var fingerprint = new Fingerprint("RecordPayment")
            .Add("source", request.Source.Source)
            .Add("reference", request.Source.Value)
            .Add("amount", request.Amount)
            .Add("valueAt", request.ValueAt)
            .Add("target", (long)request.Target.Kind)
            .Add("obligation", request.Target.Obligation?.ToString())
            .Add("account", request.Target.Account?.ToString())
            .Add("rawReference", request.Target.RawReference);
        foreach (var (key, value) in metadata)
        {
            fingerprint.Add($"metadata:{key}", value);
        }

        return fingerprint;
    }

    private static ImmutableSortedDictionary<string, string> ValidateMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        }

        if (metadata.Count > MaxMetadataEntries ||
            metadata.Any(m => string.IsNullOrWhiteSpace(m.Key) || m.Key.Length > MaxMetadataKeyLength || m.Value is null || m.Value.Length > MaxMetadataValueLength))
        {
            throw new RuleViolationException(
                ErrorCodes.InvalidRequest,
                $"Payment metadata is limited to {MaxMetadataEntries} entries, keys of 1-{MaxMetadataKeyLength} characters and values up to {MaxMetadataValueLength}.");
        }

        return metadata.ToImmutableSortedDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
    }

    private static string? Truncate(string? raw) =>
        raw is { Length: > PaymentTarget.MaxRawReferenceLength } ? raw[..PaymentTarget.MaxRawReferenceLength] : raw;
}

internal sealed partial class ExceptionsService(TenantContext context) : IPaymentExceptions
{
    private readonly CommandRunner _runner = new(context);

    public async IAsyncEnumerable<PaymentExceptionView> ListOpenAsync(ExceptionFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var all = await context.Store.ListExceptionsAsync(context.TenantId, cancellationToken).ConfigureAwait(false);
        foreach (var exception in all)
        {
            if (exception.Status != PaymentExceptionStatus.Open ||
                (filter?.Reason is { } reason && exception.Reason != reason) ||
                (filter?.OpenedBefore is { } before && exception.OpenedAt >= before))
            {
                continue;
            }

            yield return await PaymentLookup.ToViewAsync(context, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PaymentExceptionView> GetAsync(PaymentExceptionId exceptionId, CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var exception = await LoadAsync(exceptionId, cancellationToken).ConfigureAwait(false);
        return await PaymentLookup.ToViewAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }

    public Task<ResolutionResult> AssignToCustomerAsync(AssignToCustomerRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var accountRef = Validate.Required(request.Account, nameof(request.Account));
        var resolvedBy = RequireText(request.ResolvedBy, nameof(request.ResolvedBy));

        return _runner.RunAsync(
            request,
            "AssignToCustomer",
            (_, _) => Task.FromResult(new Fingerprint("AssignToCustomer")
                .Add("exception", request.ExceptionId.ToString())
                .Add("account", accountRef.ToString())
                .Add("obligation", request.Obligation?.ToString())
                .Add("resolvedBy", resolvedBy)
                .Add("note", request.Note)),
            async (tenant, ct) =>
            {
                var exception = RequireOpen(await LoadAsync(request.ExceptionId, ct).ConfigureAwait(false));
                var payment = await RequirePaymentAsync(exception, ct).ConfigureAwait(false);
                if (payment.AccountId is not null || await context.Store.GetAssignmentAsync(context.TenantId, payment.Id, ct).ConfigureAwait(false) is not null)
                {
                    throw NotResolvable($"The funds of payment {payment.Source} are already held by a customer; allocate or dismiss instead.");
                }

                var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, payment.Source.Source, ct).ConfigureAwait(false);
                var lot = pool.GetLot(payment.Id);
                if (!lot.Remaining.IsPositive)
                {
                    throw NotResolvable($"Payment {payment.Source} has no unidentified funds left to assign.");
                }

                var accountId = await context.ResolveAsync(accountRef, ct).ConfigureAwait(false);
                var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);
                var directive = await DirectiveAsync(tenant, account, request.Obligation, lot.Remaining.Currency, ct).ConfigureAwait(false);

                var now = tenant.Clock.UtcNow;
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.UnidentifiedAssigned)
                    .Debit(LedgerAccounts.Unidentified(payment.Source.Source), lot.Remaining, LedgerSourceType.Assignment, payment.Id.Value)
                    .Credit(LedgerAccounts.Unapplied(account.Id, lot.Remaining.Currency), lot.Remaining, LedgerSourceType.Assignment, payment.Id.Value);

                var updatedAccount = account.AddLot(lot with { Directive = directive });
                var allocation = directive is { } target
                    ? Allocator.Run(context, tenant, updatedAccount, payment.Id, [target], null, journalId, journal)
                    : LotAllocation.None(updatedAccount);
                updatedAccount = allocation.Account;

                var resolved = exception with { Resolution = new ExceptionResolution(ExceptionResolutionKind.AssignedToCustomer, resolvedBy, request.Note, now, journalId) };
                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [updatedAccount],
                    Pools = [pool with { Lots = pool.Lots.Remove(lot) }],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Assignments = [new PaymentAssignment(payment.Id, account.Id, lot.Remaining, journalId, now, exception.Id)],
                    Allocations = allocation.Allocations,
                    ResolvedExceptions = [resolved],
                    Events =
                    [
                        DomainEvent.Create(context.Time, "PaymentAssigned", context.TenantId, account.Id, ("paymentId", payment.Id.ToString()), ("amount", lot.Remaining.ToString())),
                        DomainEvent.Create(context.Time, "PaymentExceptionResolved", context.TenantId, account.Id, ("exceptionId", exception.Id.ToString()), ("resolution", "AssignedToCustomer")),
                        .. Allocator.Events(context, allocation),
                    ],
                };
                var result = new ResolutionResult(ViewMapper.ToView(resolved, payment, account.Id), account.Id, updatedAccount.Unapplied(lot.Remaining.Currency));
                return new Planned<ResolutionResult>(commit, result);
            },
            result => result,
            cancellationToken);
    }

    public Task<ResolutionResult> AllocateToObligationAsync(AllocateToObligationRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        var resolvedBy = RequireText(request.ResolvedBy, nameof(request.ResolvedBy));

        return _runner.RunAsync(
            request,
            "AllocateToObligation",
            (_, _) => Task.FromResult(new Fingerprint("AllocateToObligation")
                .Add("exception", request.ExceptionId.ToString())
                .Add("obligation", obligationRef.ToString())
                .Add("resolvedBy", resolvedBy)
                .Add("note", request.Note)),
            async (tenant, ct) =>
            {
                var exception = RequireOpen(await LoadAsync(request.ExceptionId, ct).ConfigureAwait(false));
                var payment = await RequirePaymentAsync(exception, ct).ConfigureAwait(false);
                var holding = await PaymentLookup.HoldingAsync(context, payment, ct).ConfigureAwait(false);
                if (holding.AccountId is not { } holder || holding.Lot is null)
                {
                    throw NotResolvable("The funds are unidentified; assign them to a customer first (optionally with the obligation).");
                }

                if (!holding.Lot.Remaining.IsPositive)
                {
                    throw NotResolvable($"Payment {payment.Source} has no unapplied funds left.");
                }

                var account = await context.LoadAccountAsync(holder, ct).ConfigureAwait(false);
                var target = await DirectiveAsync(tenant, account, obligationRef, holding.Lot.Remaining.Currency, ct).ConfigureAwait(false);

                var now = tenant.Clock.UtcNow;
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.FundsAllocated);
                var directed = account.ReplaceLot(holding.Lot with { Directive = target });
                var allocation = Allocator.Run(context, tenant, directed, payment.Id, [target!.Value], null, journalId, journal);

                var resolved = exception with
                {
                    Resolution = new ExceptionResolution(ExceptionResolutionKind.AllocatedToObligation, resolvedBy, request.Note, now, journal.IsEmpty ? null : journalId),
                };
                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [allocation.Account],
                    Journal = journal.IsEmpty ? null : journal.Build(context.TenantId, holder, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Allocations = allocation.Allocations,
                    ResolvedExceptions = [resolved],
                    Events =
                    [
                        DomainEvent.Create(context.Time, "PaymentExceptionResolved", context.TenantId, holder, ("exceptionId", exception.Id.ToString()), ("resolution", "AllocatedToObligation")),
                        .. Allocator.Events(context, allocation),
                    ],
                };
                var result = new ResolutionResult(ViewMapper.ToView(resolved, payment, holder), holder, allocation.Account.Unapplied(holding.Lot.Remaining.Currency));
                return new Planned<ResolutionResult>(commit, result);
            },
            result => result,
            cancellationToken);
    }

    public Task<ResolutionResult> DismissAsync(DismissExceptionRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var reason = RequireText(request.Reason, nameof(request.Reason));
        var resolvedBy = RequireText(request.ResolvedBy, nameof(request.ResolvedBy));

        return _runner.RunAsync(
            request,
            "DismissException",
            (_, _) => Task.FromResult(new Fingerprint("DismissException")
                .Add("exception", request.ExceptionId.ToString()).Add("reason", reason).Add("resolvedBy", resolvedBy)),
            async (tenant, ct) =>
            {
                var exception = RequireOpen(await LoadAsync(request.ExceptionId, ct).ConfigureAwait(false));
                var payment = await RequirePaymentAsync(exception, ct).ConfigureAwait(false);
                var holding = await PaymentLookup.HoldingAsync(context, payment, ct).ConfigureAwait(false);
                if (holding.AccountId is null && holding.Lot is { Remaining.IsPositive: true })
                {
                    // Invariant I-22: unidentified funds always have an open exception.
                    throw NotResolvable("The funds are still unidentified; assign them to a customer before dismissing (I-22).");
                }

                var resolved = exception with { Resolution = new ExceptionResolution(ExceptionResolutionKind.Dismissed, resolvedBy, reason, tenant.Clock.UtcNow, null) };
                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    ResolvedExceptions = [resolved],
                    Events = [DomainEvent.Create(context.Time, "PaymentExceptionResolved", context.TenantId, holding.AccountId,
                        ("exceptionId", exception.Id.ToString()), ("resolution", "Dismissed"))],
                };
                Money? unapplied = holding.AccountId is { } holder
                    ? (await context.LoadAccountAsync(holder, ct).ConfigureAwait(false)).Unapplied(payment.Amount.Currency)
                    : null;
                return new Planned<ResolutionResult>(commit, new ResolutionResult(ViewMapper.ToView(resolved, payment, holding.AccountId), holding.AccountId, unapplied));
            },
            result => result,
            cancellationToken);
    }

    private async Task<ObligationId?> DirectiveAsync(TenantSnapshot tenant, CustomerAccount account, ObligationRef? reference, Currency currency, CancellationToken ct)
    {
        if (reference is null)
        {
            return null;
        }

        var location = await context.ResolveAsync(reference, tenant, ct).ConfigureAwait(false);
        if (location.AccountId != account.Id)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {reference} does not belong to the customer.");
        }

        var obligation = account.GetObligation(location.ObligationId);
        if (!ObligationFacts.From(obligation).IsPayableIn(currency))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {obligation.PaymentReference} is not payable in {currency}.");
        }

        return obligation.Id;
    }

    private async Task<PaymentExceptionRecord> LoadAsync(PaymentExceptionId id, CancellationToken ct) =>
        await context.Store.GetExceptionAsync(context.TenantId, id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Payment exception", id.ToString());

    private async Task<PaymentRecord> RequirePaymentAsync(PaymentExceptionRecord exception, CancellationToken ct) =>
        await context.Store.GetPaymentAsync(context.TenantId, exception.PaymentId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Payment {exception.PaymentId} of exception {exception.Id} is missing.");

    private static PaymentExceptionRecord RequireOpen(PaymentExceptionRecord exception) =>
        exception.Status == PaymentExceptionStatus.Open ? exception : throw NotResolvable($"Payment exception {exception.Id} is already {exception.Status}.");

    private static RuleViolationException NotResolvable(string message) => new(ErrorCodes.ExceptionNotResolvable, message);

    private static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"{name} is required.") : value;
}
