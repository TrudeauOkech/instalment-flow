using System.Collections.Immutable;

namespace InstalmentFlow.Domain;

/// <summary>
/// An obligation: what a customer owes under one agreement. Immutable; every change produces a new instance.
/// </summary>
internal sealed record Obligation(
    ObligationId Id,
    AccountId AccountId,
    string ExternalId,
    string PaymentReference,
    Currency Currency,
    ObligationStatus Status,
    ImmutableArray<Component> Originated,
    ImmutableArray<PlanVersion> Plans,
    string? Description,
    DateTimeOffset OpenedAt)
{
    public PlanVersion? ActivePlan => Plans.SingleOrDefault(p => p.Status == PlanVersionStatus.Active);

    /// <summary>The plan whose balances are current: the active one, or the cancelled one after cancellation.</summary>
    public PlanVersion? CurrentPlan => ActivePlan ?? Plans.LastOrDefault(p => p.Status == PlanVersionStatus.Cancelled);

    public PlanVersion GetPlan(int version) => Plans.Single(p => p.Version == version);

    /// <summary>Whether amounts can still be applied to, charged to or waived on this obligation.</summary>
    public bool IsOpen => Status is ObligationStatus.Active or ObligationStatus.Settled;

    /// <summary>Replaces the active plan version and derives the status: settled once nothing is outstanding.</summary>
    public Obligation WithActivePlan(PlanVersion plan)
    {
        var current = ActivePlan ?? throw new InvalidOperationException($"Obligation {ExternalId} has no active plan.");
        return plan.Version != current.Version
            ? throw new InvalidOperationException("Only the active plan version can be updated.")
            : WithPlan(plan);
    }

    /// <summary>
    /// Replaces a plan version (active, or cancelled when a reversal re-opens a cancelled obligation's history) and
    /// derives Active ⇄ Settled from what is outstanding. Closed and cancelled obligations keep their status.
    /// </summary>
    public Obligation WithPlan(PlanVersion plan)
    {
        var updated = this with { Plans = Plans.SetItem(Plans.IndexOf(GetPlan(plan.Version)), plan) };
        if (!IsOpen || plan.Status != PlanVersionStatus.Active)
        {
            return updated;
        }

        var outstanding = plan.Instalments.Any(i => i.Outstanding.IsPositive);
        return updated with { Status = outstanding ? ObligationStatus.Active : ObligationStatus.Settled };
    }

    /// <summary>Cancels the obligation and its active plan together (ADR-011).</summary>
    public Obligation Cancel(DateTimeOffset now, string reason)
    {
        var current = ActivePlan
            ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {ExternalId} has no active plan.");
        return this with
        {
            Status = ObligationStatus.Cancelled,
            Plans = Plans.Replace(current, current.End(PlanVersionStatus.Cancelled, now, reason)),
        };
    }

    public Obligation SupersedePlan(PlanVersion replacement, DateTimeOffset now, string reason)
    {
        var current = ActivePlan
            ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {ExternalId} has no active plan.");
        if (Status != ObligationStatus.Active)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {ExternalId} is {Status}; only active obligations can change plan.");
        }

        if (current.HasFinancialHistory)
        {
            throw new RuleViolationException(
                ErrorCodes.PlanHasFinancialHistory,
                $"Plan version {current.Version} of {ExternalId} has payments or waivers applied; it cannot be superseded (ADR-011).");
        }

        return this with
        {
            Plans = Plans.Replace(current, current.End(PlanVersionStatus.Superseded, now, reason)).Add(replacement),
        };
    }
}

/// <summary>
/// The consistency boundary for every money-moving command (ADR-003): a customer within one tenant and all of
/// their obligations. Immutable; the store assigns <see cref="Version"/> on save.
/// </summary>
internal sealed record CustomerAccount(
    TenantId TenantId,
    AccountId Id,
    string ExternalId,
    long Version,
    DateTimeOffset OpenedAt,
    ImmutableArray<Obligation> Obligations,
    ImmutableArray<FundsLot> Lots)
{
    /// <summary>Version of an account that has not been saved yet.</summary>
    public const long NewVersion = 0;

    public static CustomerAccount Open(TenantId tenantId, AccountId id, string externalId, DateTimeOffset now) =>
        new(tenantId, id, externalId, NewVersion, now, [], []);

    /// <summary>The customer's unapplied funds in one currency: the sum of their lots' remaining amounts.</summary>
    public Money Unapplied(Currency currency) =>
        Money.Sum(currency, Lots.Where(l => l.Remaining.Currency == currency).Select(l => l.Remaining));

    /// <summary>Unapplied funds per currency held, ordered by currency code.</summary>
    public IReadOnlyList<Money> UnappliedByCurrency() =>
        [.. Lots.Select(l => l.Remaining.Currency).Distinct().OrderBy(c => c.Code, StringComparer.Ordinal).Select(Unapplied)];

    /// <summary>What the customer owes from reversals of funds that had already been refunded (ADR-005), per currency.</summary>
    public IReadOnlyList<Money> ReversalReceivableByCurrency() =>
        [.. Lots.Where(l => l.Deficit.IsPositive).GroupBy(l => l.Deficit.Currency).OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => Money.Sum(g.Key, g.Select(l => l.Deficit)))];

    public FundsLot GetLot(PaymentId payment) =>
        Lots.SingleOrDefault(l => l.PaymentId == payment) ?? throw new InvalidOperationException($"Account {Id} holds no funds of payment {payment}.");

    public CustomerAccount AddLot(FundsLot lot) => this with { Lots = Lots.Add(lot) };

    public CustomerAccount ReplaceLot(FundsLot lot) =>
        this with { Lots = Lots.SetItem(Lots.IndexOf(Lots.Single(l => l.PaymentId == lot.PaymentId)), lot) };

    public Obligation GetObligation(ObligationId id) =>
        Obligations.SingleOrDefault(o => o.Id == id) ?? throw new NotFoundException("Obligation", id.ToString());

    public CustomerAccount AddObligation(Obligation obligation)
    {
        if (obligation.AccountId != Id)
        {
            throw new InvalidOperationException("Obligation belongs to a different account.");
        }

        return this with { Obligations = Obligations.Add(obligation) };
    }

    public CustomerAccount ReplaceObligation(Obligation updated) =>
        this with { Obligations = Obligations.Replace(GetObligation(updated.Id), updated) };
}
