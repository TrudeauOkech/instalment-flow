using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

/// <summary>Refund postings (doc 02 §5): requested → refund payable; confirmed → out of clearing; failed → credit back.</summary>
internal static class RefundPostings
{
    public static (CustomerAccount Account, RefundRecord Refund) RequestOnAccount(
        TenantSnapshot tenant,
        CustomerAccount account,
        IReadOnlyList<(FundsLot Lot, Money Take)> takes,
        IReadOnlyDictionary<PaymentId, string> sources,
        RefundId id,
        string reason,
        string requestedBy,
        PaymentExceptionId? exceptionId,
        JournalBuilder journal)
    {
        var portions = ImmutableArray.CreateBuilder<RefundPortion>();
        foreach (var (lot, take) in takes)
        {
            account = account.ReplaceLot(lot with { Remaining = lot.Remaining - take, Refunded = lot.Refunded + take });
            journal.Debit(LedgerAccounts.Unapplied(account.Id, take.Currency), take, LedgerSourceType.Refund, id.Value);
            journal.Credit(LedgerAccounts.RefundPayable(account.Id), take, LedgerSourceType.Refund, id.Value);
            portions.Add(new RefundPortion(lot.PaymentId, sources[lot.PaymentId], take));
        }

        var amount = Money.Sum(takes[0].Take.Currency, takes.Select(t => t.Take));
        return (account, new RefundRecord(id, account.Id, null, amount, portions.ToImmutable(), RefundStatus.Requested, reason, requestedBy,
            tenant.Clock.UtcNow, exceptionId, null, null, null));
    }

    public static (UnidentifiedFunds Pool, RefundRecord Refund) RequestOnPool(
        TenantSnapshot tenant,
        UnidentifiedFunds pool,
        FundsLot lot,
        RefundId id,
        string reason,
        string requestedBy,
        PaymentExceptionId exceptionId,
        JournalBuilder journal)
    {
        var take = lot.Remaining;
        journal.Debit(LedgerAccounts.Unidentified(pool.Source), take, LedgerSourceType.Refund, id.Value);
        journal.Credit(LedgerAccounts.RefundPayable(pool.Source), take, LedgerSourceType.Refund, id.Value);
        var refund = new RefundRecord(id, null, pool.Source, take, [new RefundPortion(lot.PaymentId, pool.Source, take)], RefundStatus.Requested,
            reason, requestedBy, tenant.Clock.UtcNow, exceptionId, null, null, null);
        return (pool.ReplaceLot(lot with { Remaining = Money.Zero(take.Currency), Refunded = lot.Refunded + take }), refund);
    }

    /// <summary>
    /// Returns a failed refund's portion to its lot. Value that a later reversal had already counted as a deficit
    /// (the refund was pending when the payment was reversed) settles that deficit instead of becoming credit again.
    /// </summary>
    public static FundsLot ReturnPortion(FundsLot lot, Money amount, string payable, string unapplied, string receivable, RefundId id, JournalBuilder journal)
    {
        var settles = Money.Min(amount, lot.Deficit);
        var credit = amount - settles;
        if (settles.IsPositive)
        {
            journal.Debit(payable, settles, LedgerSourceType.Refund, id.Value);
            journal.Credit(receivable, settles, LedgerSourceType.Refund, id.Value);
        }

        if (credit.IsPositive)
        {
            journal.Debit(payable, credit, LedgerSourceType.Refund, id.Value);
            journal.Credit(unapplied, credit, LedgerSourceType.Refund, id.Value);
        }

        return lot with
        {
            Remaining = lot.Remaining + credit,
            Refunded = lot.Refunded - amount,
            Reversed = lot.Reversed + settles,
            Deficit = lot.Deficit - settles,
        };
    }
}

internal sealed class RefundsService(TenantContext context) : IRefunds
{
    private readonly CommandRunner _runner = new(context);

    public Task<RefundResult> RequestAsync(RequestRefundRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var accountRef = Validate.Required(request.Account, nameof(request.Account));
        var amount = Validate.Required(request.Amount, nameof(request.Amount));
        var reason = Validate.Text(request.Reason, nameof(request.Reason));
        var requestedBy = Validate.Text(request.RequestedBy, nameof(request.RequestedBy));
        if (!amount.IsPositive)
        {
            throw new InvalidAmountException($"Refunds must be positive; got {amount}.");
        }

        return _runner.RunAsync(
            request,
            "RequestRefund",
            (_, _) => Task.FromResult(new Fingerprint("RequestRefund")
                .Add("account", accountRef.ToString()).Add("amount", amount).Add("payment", request.Payment?.ToString())
                .Add("reason", reason).Add("requestedBy", requestedBy)),
            async (tenant, ct) =>
            {
                var accountId = await context.ResolveAsync(accountRef, ct).ConfigureAwait(false);
                var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);

                var candidates = new List<FundsLot>();
                if (request.Payment is { } paymentRef)
                {
                    var payment = await PaymentLookup.FindAsync(context, paymentRef, ct).ConfigureAwait(false) ?? throw new NotFoundException("Payment", paymentRef.ToString());
                    var lot = account.Lots.SingleOrDefault(l => l.PaymentId == payment.Id)
                        ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Payment {paymentRef} is not held by the customer.");
                    if (await Allocator.HasOpenExceptionAsync(context, payment, ct).ConfigureAwait(false))
                    {
                        throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, $"Payment {paymentRef} has an open exception; use MarkForRefund.");
                    }

                    candidates.Add(lot);
                }
                else
                {
                    foreach (var lot in account.Lots.Where(l => l.Remaining.Currency == amount.Currency && l.Remaining.IsPositive))
                    {
                        if (!await Allocator.HasOpenExceptionAsync(context, lot.PaymentId, ct).ConfigureAwait(false))
                        {
                            candidates.Add(lot);
                        }
                    }
                }

                if (candidates.Any(l => l.Remaining.Currency != amount.Currency))
                {
                    throw new CurrencyMismatchException(candidates[0].Remaining.Currency, amount.Currency);
                }

                var takes = Corrections.TakeOldestFirst(candidates, amount);
                var sources = await SourcesAsync(takes.Select(t => t.Lot.PaymentId), ct).ConfigureAwait(false);
                var id = RefundId.New(context.Time);
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.RefundRequested);
                var (updated, refund) = RefundPostings.RequestOnAccount(tenant, account, takes, sources, id, reason, requestedBy, null, journal);

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [updated],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, tenant.Clock.UtcNow, context.Time, journalId),
                    Refunds = [refund],
                    Events = [DomainEvent.Create(context.Time, "CreditRefundRequested", context.TenantId, account.Id, ("refundId", id.ToString()), ("amount", amount.ToString()))],
                };
                return new Planned<RefundResult>(commit, new RefundResult(refund.ToView(), updated.Unapplied(amount.Currency)));
            },
            result => result,
            cancellationToken);
    }

    public Task<RefundResult> CompleteAsync(CompleteRefundRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        if (!Enum.IsDefined(request.Completion))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Completion must be Confirmed or Failed.");
        }

        var failureReason = request.Completion == RefundCompletion.Failed ? Validate.Text(request.FailureReason, nameof(request.FailureReason)) : null;

        return _runner.RunAsync(
            request,
            "CompleteRefund",
            (_, _) => Task.FromResult(new Fingerprint("CompleteRefund")
                .Add("refund", request.Refund.ToString()).Add("completion", (long)request.Completion)
                .Add("providerReference", request.ProviderReference?.ToString()).Add("failureReason", failureReason)),
            async (tenant, ct) =>
            {
                var refund = await context.Store.GetRefundAsync(context.TenantId, request.Refund, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException("Refund", request.Refund.ToString());
                if (refund.Status != RefundStatus.Requested)
                {
                    throw new RuleViolationException(ErrorCodes.RefundNotPending, $"Refund {refund.Id} is already {refund.Status}.");
                }

                var now = tenant.Clock.UtcNow;
                var confirmed = request.Completion == RefundCompletion.Confirmed;
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(confirmed ? JournalKind.RefundConfirmed : JournalKind.RefundFailed);
                var completed = refund with
                {
                    Status = confirmed ? RefundStatus.Confirmed : RefundStatus.Failed,
                    CompletedAt = now,
                    ProviderReference = request.ProviderReference,
                    FailureReason = failureReason,
                };
                var commit = new Commit { Tenant = context.TenantId, Refunds = [completed] };
                var payable = refund.AccountId is { } owner ? LedgerAccounts.RefundPayable(owner) : LedgerAccounts.RefundPayable(refund.UnidentifiedSource!);
                Money? unappliedAfter = null;
                var events = new List<DomainEvent>
                {
                    DomainEvent.Create(context.Time, confirmed ? "CreditRefundConfirmed" : "CreditRefundFailed", context.TenantId, refund.AccountId,
                        ("refundId", refund.Id.ToString()), ("amount", refund.Amount.ToString())),
                };

                if (confirmed)
                {
                    foreach (var portion in refund.Portions)
                    {
                        journal.Debit(payable, portion.Amount, LedgerSourceType.Refund, refund.Id.Value);
                        journal.Credit(LedgerAccounts.Clearing(portion.Source), portion.Amount, LedgerSourceType.Refund, refund.Id.Value);
                    }
                }
                else if (refund.AccountId is { } accountId)
                {
                    var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);
                    foreach (var portion in refund.Portions)
                    {
                        var lot = RefundPostings.ReturnPortion(account.GetLot(portion.PaymentId), portion.Amount, payable,
                            LedgerAccounts.Unapplied(accountId, portion.Amount.Currency), LedgerAccounts.ReversalReceivable(accountId, portion.Amount.Currency), refund.Id, journal);
                        account = account.ReplaceLot(lot);
                    }

                    commit = commit with { Accounts = [account] };
                    unappliedAfter = account.Unapplied(refund.Amount.Currency);
                    var reopened = await ReopenAsync(tenant, refund, account.GetLot(refund.Portions[0].PaymentId), ct).ConfigureAwait(false);
                    commit = commit with { NewExceptions = reopened };
                }
                else
                {
                    var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, refund.UnidentifiedSource!, ct).ConfigureAwait(false);
                    var portion = refund.Portions[0];
                    var lot = RefundPostings.ReturnPortion(pool.GetLot(portion.PaymentId), portion.Amount, payable,
                        LedgerAccounts.Unidentified(pool.Source), LedgerAccounts.ReversalReceivable(pool.Source), refund.Id, journal);
                    commit = commit with { Pools = [pool.ReplaceLot(lot)], NewExceptions = await ReopenAsync(tenant, refund, lot, ct).ConfigureAwait(false) };
                }

                commit = commit with
                {
                    Journal = journal.Build(context.TenantId, refund.AccountId, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Events = [.. events, .. commit.NewExceptions.Select(e => DomainEvent.Create(context.Time, "PaymentExceptionRaised", context.TenantId, refund.AccountId,
                        ("exceptionId", e.Id.ToString()), ("paymentId", e.PaymentId.ToString()), ("reason", e.Reason.ToString())))],
                };
                return new Planned<RefundResult>(commit, new RefundResult(completed.ToView(), unappliedAfter));
            },
            result => result,
            cancellationToken);
    }

    public async Task<RefundView> GetAsync(RefundId refundId, CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var refund = await context.Store.GetRefundAsync(context.TenantId, refundId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Refund", refundId.ToString());
        return refund.ToView();
    }

    /// <summary>
    /// A failed refund that resolved a payment exception puts the funds back where they were, so they need
    /// attention again: a new exception is raised (and unidentified funds always have one, I-22).
    /// </summary>
    private async Task<ImmutableArray<PaymentExceptionRecord>> ReopenAsync(TenantSnapshot tenant, RefundRecord refund, FundsLot lot, CancellationToken ct)
    {
        if (refund.ExceptionId is null || !lot.Remaining.IsPositive)
        {
            return [];
        }

        var payment = await context.Store.GetPaymentAsync(context.TenantId, lot.PaymentId, ct).ConfigureAwait(false);
        return
        [
            new PaymentExceptionRecord(PaymentExceptionId.New(context.Time), lot.PaymentId, PaymentExceptionReason.RefundFailed,
                $"The refund of {refund.Amount} failed and the funds are held again. {refund.FailureReason ?? string.Empty}".Trim(),
                payment?.RawReference, [], tenant.Clock.UtcNow, null),
        ];
    }

    private async Task<Dictionary<PaymentId, string>> SourcesAsync(IEnumerable<PaymentId> payments, CancellationToken ct)
    {
        var sources = new Dictionary<PaymentId, string>();
        foreach (var id in payments)
        {
            var payment = await context.Store.GetPaymentAsync(context.TenantId, id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Payment {id} is missing.");
            sources[id] = payment.Source.Source;
        }

        return sources;
    }
}

internal sealed partial class ExceptionsService
{
    public Task<ResolutionResult> MarkForRefundAsync(MarkForRefundRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var reason = Validate.Text(request.Reason, nameof(request.Reason));
        var resolvedBy = Validate.Text(request.ResolvedBy, nameof(request.ResolvedBy));

        return _runner.RunAsync(
            request,
            "MarkForRefund",
            (_, _) => Task.FromResult(new Fingerprint("MarkForRefund")
                .Add("exception", request.ExceptionId.ToString()).Add("reason", reason).Add("resolvedBy", resolvedBy).Add("note", request.Note)),
            async (tenant, ct) =>
            {
                var exception = RequireOpen(await LoadAsync(request.ExceptionId, ct).ConfigureAwait(false));
                var payment = await RequirePaymentAsync(exception, ct).ConfigureAwait(false);
                var holding = await PaymentLookup.HoldingAsync(context, payment, ct).ConfigureAwait(false);
                if (holding.Lot is not { Remaining.IsPositive: true } lot)
                {
                    throw NotResolvable($"Payment {payment.Source} has no funds left to refund.");
                }

                var now = tenant.Clock.UtcNow;
                var id = RefundId.New(context.Time);
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.RefundRequested);
                var resolved = exception with { Resolution = new ExceptionResolution(ExceptionResolutionKind.MarkedForRefund, resolvedBy, request.Note, now, journalId) };
                var commit = new Commit { Tenant = context.TenantId, ResolvedExceptions = [resolved] };
                RefundRecord refund;
                Money? unapplied = null;
                if (holding.AccountId is { } holder)
                {
                    var account = await context.LoadAccountAsync(holder, ct).ConfigureAwait(false);
                    (account, refund) = RefundPostings.RequestOnAccount(tenant, account, [(lot, lot.Remaining)],
                        new Dictionary<PaymentId, string> { [payment.Id] = payment.Source.Source }, id, reason, resolvedBy, exception.Id, journal);
                    commit = commit with { Accounts = [account] };
                    unapplied = account.Unapplied(lot.Remaining.Currency);
                }
                else
                {
                    var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, payment.Source.Source, ct).ConfigureAwait(false);
                    (pool, refund) = RefundPostings.RequestOnPool(tenant, pool, lot, id, reason, resolvedBy, exception.Id, journal);
                    commit = commit with { Pools = [pool] };
                }

                commit = commit with
                {
                    Journal = journal.Build(context.TenantId, holding.AccountId, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Refunds = [refund],
                    Events =
                    [
                        DomainEvent.Create(context.Time, "CreditRefundRequested", context.TenantId, holding.AccountId, ("refundId", id.ToString()), ("amount", refund.Amount.ToString())),
                        DomainEvent.Create(context.Time, "PaymentExceptionResolved", context.TenantId, holding.AccountId, ("exceptionId", exception.Id.ToString()), ("resolution", "MarkedForRefund")),
                    ],
                };
                var result = new ResolutionResult(ViewMapper.ToView(resolved, payment, holding.AccountId), holding.AccountId, unapplied) { RefundId = id };
                return new Planned<ResolutionResult>(commit, result);
            },
            result => result,
            cancellationToken);
    }
}
