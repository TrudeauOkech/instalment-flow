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
    ImmutableArray<Obligation> Obligations)
{
    /// <summary>Version of an account that has not been saved yet.</summary>
    public const long NewVersion = 0;

    public static CustomerAccount Open(TenantId tenantId, AccountId id, string externalId, DateTimeOffset now) =>
        new(tenantId, id, externalId, NewVersion, now, []);

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
