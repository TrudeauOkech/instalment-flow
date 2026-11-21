using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

/// <summary>Waiver and write-off postings: Dr waiver:{component} / Cr the obligation's receivable (doc 02 §5).</summary>
internal static class WaiverPostings
{
    public static (Obligation Obligation, ImmutableArray<WaiverRecord> Records) Apply(
        Obligation obligation,
        IReadOnlyList<(Instalment Instalment, Money Amount)> lines,
        ComponentKey key,
        WaiverKind kind,
        WaiverId id,
        string reason,
        JournalTxId journalId,
        JournalBuilder journal,
        DateTimeOffset now)
    {
        var plan = obligation.ActivePlan!;
        var records = ImmutableArray.CreateBuilder<WaiverRecord>(lines.Count);
        foreach (var (instalment, amount) in lines)
        {
            plan = plan.Waive(instalment.Sequence, key, amount, now);
            records.Add(new WaiverRecord(id, obligation.AccountId, obligation.Id, plan.Version, instalment.Sequence, instalment.DueDate, key, amount, kind, reason, journalId, now));
            journal.Debit(LedgerAccounts.Waiver(key), amount, LedgerSourceType.Waiver, id.Value);
            journal.Credit(LedgerAccounts.Receivable(obligation.Id, key), amount, LedgerSourceType.Waiver, id.Value);
        }

        return (obligation.WithActivePlan(plan), records.ToImmutable());
    }

    /// <summary>Fills <paramref name="amount"/> over the instalments' outstanding balance of one component, in the given order.</summary>
    public static List<(Instalment Instalment, Money Amount)> Fill(IEnumerable<Instalment> instalments, ComponentKey key, Money amount)
    {
        var lines = new List<(Instalment, Money)>();
        var left = amount;
        foreach (var instalment in instalments)
        {
            var outstanding = instalment.Components.SingleOrDefault(c => c.Key == key)?.Outstanding;
            if (!left.IsPositive || outstanding is not { IsPositive: true })
            {
                continue;
            }

            var take = Money.Min(left, outstanding);
            lines.Add((instalment, take));
            left -= take;
        }

        return lines;
    }

    public static WaiverLineView ToView(WaiverRecord record) => new(record.InstalmentSequence, record.DueDate, record.Component, record.Amount);
}

internal sealed class ChargesService(TenantContext context) : ICharges
{
    private readonly CommandRunner _runner = new(context);

    public Task<ChargeResult> PostAsync(PostChargeRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        var amount = Validate.Required(request.Amount, nameof(request.Amount));
        var description = Validate.Text(request.Description, nameof(request.Description));
        var due = Validate.Required(request.DuePolicy, nameof(request.DuePolicy));
        var key = Key(request.Type, request.ChargeCode);
        RequirePositive(amount);

        return _runner.RunAsync(
            request,
            "PostCharge",
            (_, _) => Task.FromResult(new Fingerprint("PostCharge")
                .Add("obligation", obligationRef.ToString()).Add("component", key.ToString()).Add("amount", amount)
                .Add("description", description).Add("due", due.ToString())),
            async (tenant, ct) =>
            {
                var (account, obligation) = await LoadAsync(tenant, obligationRef, ct).ConfigureAwait(false);
                RequireOpen(obligation);
                RequireCurrency(obligation, amount);
                var plan = obligation.ActivePlan ?? throw NotOpen(obligation);
                var instalment = Choose(plan, due.Kind == ChargeDueKind.PerTenantPolicy ? tenant.Policy.ChargeDue : due, tenant.Clock.Today);

                var now = tenant.Clock.UtcNow;
                var id = ChargeId.New(context.Time);
                var journalId = JournalTxId.New(context.Time);
                var updated = obligation.WithActivePlan(plan.AddCharge(instalment.Sequence, key, amount, now));
                var record = new ChargeRecord(id, account.Id, obligation.Id, plan.Version, instalment.Sequence, instalment.DueDate, key, amount, description, journalId, now);
                var journal = new JournalBuilder(JournalKind.ChargePosted)
                    .Debit(LedgerAccounts.Receivable(obligation.Id, key), amount, LedgerSourceType.Charge, id.Value)
                    .Credit(LedgerAccounts.Origination(key), amount, LedgerSourceType.Charge, id.Value);

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account.ReplaceObligation(updated)],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Charges = [record],
                    Events = [DomainEvent.Create(context.Time, "ChargePosted", context.TenantId, account.Id,
                        ("chargeId", id.ToString()), ("obligationId", obligation.Id.ToString()), ("component", key.ToString()), ("amount", amount.ToString()))],
                };
                return new Planned<ChargeResult>(commit, ToResult(record, false, updated, []));
            },
            result => result,
            cancellationToken);
    }

    public Task<ChargeResult> ReverseAsync(ReverseChargeRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var reason = Validate.Text(request.Reason, nameof(request.Reason));

        return _runner.RunAsync(
            request,
            "ReverseCharge",
            (_, _) => Task.FromResult(new Fingerprint("ReverseCharge").Add("charge", request.Charge.ToString()).Add("reason", reason)),
            async (tenant, ct) =>
            {
                var charge = await context.Store.GetChargeAsync(context.TenantId, request.Charge, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException("Charge", request.Charge.ToString());
                if (await context.Store.GetChargeReversalAsync(context.TenantId, charge.Id, ct).ConfigureAwait(false) is not null)
                {
                    throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Charge {charge.Id} is already reversed.");
                }

                var account = await context.LoadAccountAsync(charge.AccountId, ct).ConfigureAwait(false);
                var obligation = account.GetObligation(charge.ObligationId);
                RequireOpen(obligation);

                var now = tenant.Clock.UtcNow;
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.ChargeReversed);
                var component = obligation.GetPlan(charge.PlanVersion).GetInstalment(charge.InstalmentSequence).Components.Single(c => c.Key == charge.Component);

                // Make room: allocations that paid this instalment component are unwound, most recent first; their funds stay unapplied.
                var unwinds = ImmutableArray<AllocationReversalRecord>.Empty;
                if (component.Outstanding < charge.Amount)
                {
                    var allocations = await context.Store.ListAccountAllocationsAsync(context.TenantId, account.Id, ct).ConfigureAwait(false);
                    var reversals = await context.Store.ListAccountAllocationReversalsAsync(context.TenantId, account.Id, ct).ConfigureAwait(false);
                    var lifo = Corrections.Active(
                        allocations.Where(a => a.ObligationId == charge.ObligationId && a.PlanVersion == charge.PlanVersion &&
                                               a.InstalmentSequence == charge.InstalmentSequence && a.Component == charge.Component),
                        reversals);
                    lifo.Reverse();
                    (account, unwinds) = Corrections.Unwind(account, lifo, charge.Amount - component.Outstanding, UnwindCause.ChargeReversal, charge.Id.Value, journalId, now, context.Time);
                    foreach (var unwind in unwinds)
                    {
                        journal.Debit(LedgerAccounts.Receivable(unwind.ObligationId, unwind.Component), unwind.Amount, LedgerSourceType.AllocationReversal, unwind.Id);
                        journal.Credit(LedgerAccounts.Unapplied(account.Id, unwind.Amount.Currency), unwind.Amount, LedgerSourceType.AllocationReversal, unwind.Id);
                    }
                }

                obligation = account.GetObligation(charge.ObligationId);
                var plan = obligation.GetPlan(charge.PlanVersion);
                if (plan.GetInstalment(charge.InstalmentSequence).Components.Single(c => c.Key == charge.Component).Outstanding < charge.Amount)
                {
                    throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Charge {charge.Id} has been partly waived; it cannot be reversed in full.");
                }

                obligation = obligation.WithPlan(plan.RemoveCharge(charge.InstalmentSequence, charge.Component, charge.Amount, now));
                account = account.ReplaceObligation(obligation);
                journal.Debit(LedgerAccounts.Origination(charge.Component), charge.Amount, LedgerSourceType.Charge, charge.Id.Value);
                journal.Credit(LedgerAccounts.Receivable(charge.ObligationId, charge.Component), charge.Amount, LedgerSourceType.Charge, charge.Id.Value);

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    ChargeReversals = [new ChargeReversalRecord(charge.Id, reason, journalId, now)],
                    AllocationReversals = unwinds,
                    Events =
                    [
                        DomainEvent.Create(context.Time, "ChargeReversed", context.TenantId, account.Id, ("chargeId", charge.Id.ToString()), ("amount", charge.Amount.ToString())),
                        .. unwinds.Length == 0 ? [] : new[]
                        {
                            DomainEvent.Create(context.Time, "AllocationReversed", context.TenantId, account.Id, ("obligationId", charge.ObligationId.ToString()),
                                ("amount", Money.Sum(charge.Amount.Currency, unwinds.Select(u => u.Amount)).ToString())),
                        },
                    ],
                };
                return new Planned<ChargeResult>(commit, ToResult(charge, true, obligation, unwinds));
            },
            result => result,
            cancellationToken);
    }

    public Task<WaiverResult> WaiveAsync(WaiveRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        var key = Validate.Required(request.Component, nameof(request.Component));
        var amount = Validate.Required(request.Amount, nameof(request.Amount));
        var reason = Validate.Text(request.Reason, nameof(request.Reason));
        RequirePositive(amount);

        return _runner.RunAsync(
            request,
            "Waive",
            (_, _) => Task.FromResult(new Fingerprint("Waive")
                .Add("obligation", obligationRef.ToString()).Add("component", key.ToString()).Add("amount", amount)
                .Add("reason", reason).Add("instalment", request.Instalment ?? 0)),
            async (tenant, ct) =>
            {
                var (account, obligation) = await LoadAsync(tenant, obligationRef, ct).ConfigureAwait(false);
                RequireOpen(obligation);
                RequireCurrency(obligation, amount);
                var plan = obligation.ActivePlan ?? throw NotOpen(obligation);
                var instalments = request.Instalment is { } sequence
                    ? [plan.Instalments.SingleOrDefault(i => i.Sequence == sequence) ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Instalment {sequence} does not exist.")]
                    : plan.Instalments.OrderBy(i => i.DueDate).ThenBy(i => i.Sequence).ToList();
                var lines = WaiverPostings.Fill(instalments, key, amount);
                var covered = Money.Sum(amount.Currency, lines.Select(l => l.Amount));
                if (covered != amount)
                {
                    throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Only {covered} of {key} is outstanding there; {amount} cannot be waived (I-6).");
                }

                var now = tenant.Clock.UtcNow;
                var id = WaiverId.New(context.Time);
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.AmountWaived);
                var (updated, records) = WaiverPostings.Apply(obligation, lines, key, WaiverKind.Waiver, id, reason, journalId, journal, now);

                var events = new List<DomainEvent>
                {
                    DomainEvent.Create(context.Time, "AmountWaived", context.TenantId, account.Id,
                        ("waiverId", id.ToString()), ("obligationId", obligation.Id.ToString()), ("component", key.ToString()), ("amount", amount.ToString())),
                };
                if (updated.Status == ObligationStatus.Settled && obligation.Status != ObligationStatus.Settled)
                {
                    events.Add(DomainEvent.Create(context.Time, "ObligationSettled", context.TenantId, account.Id, ("obligationId", obligation.Id.ToString())));
                }

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account.ReplaceObligation(updated)],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Waivers = records,
                    Events = [.. events],
                };
                return new Planned<WaiverResult>(commit, new WaiverResult(id, obligation.Id, [.. records.Select(WaiverPostings.ToView)], ViewMapper.Outstanding(updated), updated.Status));
            },
            result => result,
            cancellationToken);
    }

    /// <summary>Chooses the instalment a charge is added to (ADR-008).</summary>
    internal static Instalment Choose(PlanVersion plan, ChargeDue due, DateOnly today)
    {
        var ordered = plan.Instalments.OrderBy(i => i.Sequence).ToList();
        return due.Kind switch
        {
            ChargeDueKind.OnInstalment => ordered.SingleOrDefault(i => i.Sequence == due.Sequence)
                ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Instalment {due.Sequence} does not exist."),
            ChargeDueKind.Immediately => ordered.LastOrDefault(i => i.DueDate <= today) ?? ordered[0],
            _ => ordered.FirstOrDefault(i => i.Outstanding.IsPositive && i.DueDate >= today)
                 ?? ordered.LastOrDefault(i => i.Outstanding.IsPositive)
                 ?? ordered[^1],
        };
    }

    private static ComponentKey Key(ChargeType type, string? chargeCode)
    {
        if (!Enum.IsDefined(type))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "The charge type must be Interest, Fee or Penalty.");
        }

        try
        {
            return new ComponentKey((ComponentType)type, string.IsNullOrEmpty(chargeCode) ? null : chargeCode);
        }
        catch (ArgumentException ex)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, ex.Message);
        }
    }

    private static void RequirePositive(Money amount)
    {
        if (!amount.IsPositive)
        {
            throw new InvalidAmountException($"The amount must be positive; got {amount}.");
        }
    }

    private static void RequireCurrency(Obligation obligation, Money amount)
    {
        if (amount.Currency != obligation.Currency)
        {
            throw new CurrencyMismatchException(obligation.Currency, amount.Currency);
        }
    }

    internal static void RequireOpen(Obligation obligation)
    {
        if (!obligation.IsOpen)
        {
            throw NotOpen(obligation);
        }
    }

    private static RuleViolationException NotOpen(Obligation obligation) =>
        new(ErrorCodes.ObligationNotOpen, $"Obligation {obligation.ExternalId} is {obligation.Status}.");

    private static ChargeResult ToResult(ChargeRecord charge, bool reversed, Obligation obligation, ImmutableArray<AllocationReversalRecord> unwinds) =>
        new(charge.Id, charge.ObligationId, charge.Component, charge.Amount, charge.InstalmentSequence, charge.DueDate, reversed,
            ViewMapper.Outstanding(obligation), [.. unwinds.Select(Corrections.ToView)]);

    private async Task<(CustomerAccount Account, Obligation Obligation)> LoadAsync(TenantSnapshot tenant, ObligationRef reference, CancellationToken ct)
    {
        var location = await context.ResolveAsync(reference, tenant, ct).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(location.AccountId, ct).ConfigureAwait(false);
        return (account, account.GetObligation(location.ObligationId));
    }
}

internal sealed partial class ObligationsService
{
    public Task<ObligationCancelled> CancelAsync(CancelObligationRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        var reason = Validate.Text(request.Reason, nameof(request.Reason));
        var writeOffs = Validate.Required(request.WriteOffs, nameof(request.WriteOffs));
        if (writeOffs.Any(w => w is null) || writeOffs.Select(w => w.Key).Distinct().Count() != writeOffs.Count)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Each component may be written off once.");
        }

        return _runner.RunAsync(
            request,
            "CancelObligation",
            (_, _) =>
            {
                var fingerprint = new Fingerprint("CancelObligation").Add("obligation", obligationRef.ToString()).Add("reason", reason);
                foreach (var writeOff in writeOffs.OrderBy(w => w.Key))
                {
                    fingerprint.Add($"writeOff:{writeOff.Key}", writeOff.Amount);
                }

                return Task.FromResult(fingerprint);
            },
            async (tenant, ct) =>
            {
                var location = await context.ResolveAsync(obligationRef, tenant, ct).ConfigureAwait(false);
                var account = await context.LoadAccountAsync(location.AccountId, ct).ConfigureAwait(false);
                var obligation = account.GetObligation(location.ObligationId);
                ChargesService.RequireOpen(obligation);
                var plan = obligation.ActivePlan
                    ?? throw new RuleViolationException(ErrorCodes.ObligationNotOpen, $"Obligation {obligation.ExternalId} has no active plan.");

                // ADR-011: outstanding balances are cleared only by explicit write-off lines, exactly matching them.
                var outstanding = plan.Instalments.SelectMany(i => i.Components).GroupBy(c => c.Key).OrderBy(g => g.Key)
                    .Select(g => new { g.Key, Amount = Money.Sum(obligation.Currency, g.Select(c => c.Outstanding)) })
                    .Where(o => o.Amount.IsPositive).ToList();
                var matches = outstanding.Count == writeOffs.Count &&
                              outstanding.All(o => writeOffs.Any(w => w.Key == o.Key && w.Amount == o.Amount));
                if (!matches)
                {
                    var expected = outstanding.Count == 0 ? "none" : string.Join(", ", outstanding.Select(o => $"{o.Key} {o.Amount}"));
                    throw new RuleViolationException(ErrorCodes.WriteOffMismatch, $"The write-off lines must equal what is outstanding on {obligation.ExternalId}: {expected}.");
                }

                var now = tenant.Clock.UtcNow;
                var id = WaiverId.New(context.Time);
                var journalId = JournalTxId.New(context.Time);
                var journal = new JournalBuilder(JournalKind.ObligationCancelled);
                var waivers = ImmutableArray.CreateBuilder<WaiverRecord>();
                foreach (var writeOff in writeOffs)
                {
                    var lines = WaiverPostings.Fill(obligation.ActivePlan!.Instalments.OrderBy(i => i.Sequence), writeOff.Key, writeOff.Amount);
                    (obligation, var records) = WaiverPostings.Apply(obligation, lines, writeOff.Key, WaiverKind.WriteOff, id, reason, journalId, journal, now);
                    waivers.AddRange(records);
                }

                var cancelled = obligation.Cancel(now, reason);
                var updated = account.ReplaceObligation(cancelled);
                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [updated],
                    Journal = journal.IsEmpty ? null : journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time, journalId),
                    Waivers = waivers.ToImmutable(),
                    Events = [DomainEvent.Create(context.Time, "ObligationCancelled", context.TenantId, account.Id,
                        ("obligationId", cancelled.Id.ToString()), ("planVersion", plan.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                };
                return new Planned<ObligationCancelled>(commit, new ObligationCancelled(cancelled.Id, plan.Version, [.. writeOffs.OrderBy(w => w.Key)], updated.UnappliedByCurrency()));
            },
            result => result,
            cancellationToken);
    }

    public async Task<SettlementQuote> QuoteEarlySettlementAsync(QuoteSettlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var location = await context.ResolveAsync(obligationRef, tenant, cancellationToken).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(location.AccountId, cancellationToken).ConfigureAwait(false);
        var obligation = account.GetObligation(location.ObligationId);
        var asOf = request.AsOfDate ?? tenant.Clock.Today;
        var currency = obligation.Currency;
        var instalments = obligation.CurrentPlan?.Instalments ?? [];

        var components = instalments
            .SelectMany(i => i.Components.Select(c => (Instalment: i, Component: c)))
            .GroupBy(x => x.Component.Key)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var due = Money.Sum(currency, g.Where(x => x.Instalment.DueDate <= asOf).Select(x => x.Component.Outstanding));
                var notYet = Money.Sum(currency, g.Where(x => x.Instalment.DueDate > asOf).Select(x => x.Component.Outstanding));
                return new SettlementComponentView(g.Key, due, notYet, due + notYet);
            })
            .Where(c => c.Outstanding.IsPositive)
            .ToList();

        var outstanding = Money.Sum(currency, components.Select(c => c.Outstanding));
        var unapplied = account.Unapplied(currency);
        return new SettlementQuote(
            obligation.Id,
            asOf,
            components,
            Money.Sum(currency, components.Select(c => c.Due)),
            Money.Sum(currency, components.Select(c => c.NotYetDue)),
            outstanding,
            unapplied,
            Money.Max(Money.Zero(currency), outstanding - unapplied));
    }
}
