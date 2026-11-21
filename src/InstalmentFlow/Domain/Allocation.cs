using System.Collections.Immutable;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Domain;

internal readonly record struct AllocationId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>An applied allocation: an immutable fact (ADR-004). Undone only by an allocation reversal.</summary>
internal sealed record AllocationRecord(
    AllocationId Id,
    PaymentId PaymentId,
    AccountId AccountId,
    ObligationId ObligationId,
    int PlanVersion,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount,
    StrategyRef Strategy,
    int PolicyVersion,
    JournalTxId JournalTxId,
    DateTimeOffset CreatedAt);

internal sealed record LotAllocation(
    CustomerAccount Account,
    ImmutableArray<AllocationRecord> Allocations,
    ImmutableArray<InstalmentRef> InstalmentsSettled,
    ImmutableArray<ObligationId> ObligationsSettled)
{
    public static LotAllocation None(CustomerAccount account) => new(account, [], [], []);
}

/// <summary>Raised when a strategy's plan breaks a rule (ADR-006); carries <c>IF2002</c>.</summary>
internal static class PlanValidator
{
    public static void Validate(AllocationPlan? plan, AllocationContext context, StrategyRef strategy)
    {
        if (plan?.Lines is null)
        {
            throw Invalid(strategy, "returned no plan");
        }

        var allowed = context.Targets.ToHashSet();
        var seen = new HashSet<AllocationTarget>();
        var total = Money.Zero(context.Amount.Currency);
        foreach (var line in plan.Lines)
        {
            if (line?.Target is null || line.Amount is null)
            {
                throw Invalid(strategy, "returned an empty line");
            }

            if (!allowed.Contains(line.Target))
            {
                throw Invalid(strategy, $"targeted {line.Target.ObligationId} #{line.Target.InstalmentSequence} {line.Target.Component}, which it may not pay");
            }

            if (!seen.Add(line.Target))
            {
                throw Invalid(strategy, $"targeted {line.Target.ObligationId} #{line.Target.InstalmentSequence} {line.Target.Component} more than once");
            }

            if (line.Amount.Currency != context.Amount.Currency || !line.Amount.IsPositive || line.Amount > line.Target.Outstanding)
            {
                throw Invalid(strategy, $"allocated {line.Amount} to a target with {line.Target.Outstanding} outstanding");
            }

            total += line.Amount;
        }

        if (total > context.Amount)
        {
            throw Invalid(strategy, $"allocated {total} from {context.Amount} of funds");
        }
    }

    private static RuleViolationException Invalid(StrategyRef strategy, string what) =>
        new(ErrorCodes.InvalidAllocationPlan, $"Allocation strategy {strategy} {what}; the plan was rejected (ADR-006).");
}

/// <summary>
/// Allocates one funds lot within a customer account (ADR-005, ADR-006). Pure apart from ID generation: strategy,
/// validation, the future-instalment policy, and updates to instalments, lot and obligation status.
/// </summary>
internal static class AllocationEngine
{
    public static readonly StrategyRef NextInstalments = new("future.next-instalments", 1);
    public static readonly StrategyRef PrepayPrincipal = new("future.prepay-principal", 1);

    public static LotAllocation Allocate(
        CustomerAccount account,
        PaymentId lotId,
        IReadOnlyCollection<ObligationId> eligible,
        IAllocationStrategy strategy,
        TenantPolicy policy,
        int policyVersion,
        DateOnly asOf,
        JournalTxId journalTxId,
        DateTimeOffset now,
        TimeProvider time)
    {
        var lot = account.Lots.Single(l => l.PaymentId == lotId);
        if (!lot.Remaining.IsPositive || eligible.Count == 0)
        {
            return LotAllocation.None(account);
        }

        var currency = lot.Remaining.Currency;
        var obligations = account.Obligations
            .Where(o => eligible.Contains(o.Id) && o.Status == ObligationStatus.Active && o.Currency == currency && o.ActivePlan is not null)
            .ToList();
        var open = OpenTargets(obligations, asOf, policy.ComponentOrder).ToList();
        var due = open.Where(t => t.Target.DueDate <= asOf).ToList();
        var context = new AllocationContext(
            lot.Remaining,
            asOf,
            [.. TargetOrder.InstalmentThenComponent(new AllocationContext(lot.Remaining, asOf, [], policy.ComponentOrder), due.Select(t => t.Target))],
            policy.ComponentOrder);

        var plan = strategy.Plan(context);
        PlanValidator.Validate(plan, context, strategy.Ref);

        var lines = plan.Lines.Select(l => (l.Target, l.Amount, Strategy: strategy.Ref)).ToList();
        var remainder = lot.Remaining - Money.Sum(currency, plan.Lines.Select(l => l.Amount));
        if (remainder.IsPositive && policy.FutureInstalments != FutureInstalmentPolicy.HoldAsCredit)
        {
            var future = open.Where(t => t.Target.DueDate > asOf).Select(t => t.Target);
            var (order, reference) = policy.FutureInstalments == FutureInstalmentPolicy.ApplyToNextInstalments
                ? (TargetOrder.InstalmentThenComponent(context, future), NextInstalments)
                : (future.Where(t => t.Component.Type == ComponentType.Principal)
                    .OrderBy(t => t.ObligationOpenedAt).ThenBy(t => t.ObligationId.Value)
                    .ThenByDescending(t => t.DueDate).ThenByDescending(t => t.InstalmentSequence), PrepayPrincipal);
            var futurePlan = AllocationPlan.FillInOrder(context with { Amount = remainder }, order);
            lines.AddRange(futurePlan.Lines.Select(l => (l.Target, l.Amount, Strategy: reference)));
        }

        return Apply(account, lot, lines, policyVersion, journalTxId, now, time);
    }

    private static LotAllocation Apply(
        CustomerAccount account,
        FundsLot lot,
        List<(AllocationTarget Target, Money Amount, StrategyRef Strategy)> lines,
        int policyVersion,
        JournalTxId journalTxId,
        DateTimeOffset now,
        TimeProvider time)
    {
        if (lines.Count == 0)
        {
            return LotAllocation.None(account);
        }

        var records = ImmutableArray.CreateBuilder<AllocationRecord>(lines.Count);
        var settled = ImmutableArray.CreateBuilder<InstalmentRef>();
        var obligationsSettled = ImmutableArray.CreateBuilder<ObligationId>();
        var total = Money.Zero(lot.Remaining.Currency);

        foreach (var byObligation in lines.GroupBy(l => l.Target.ObligationId))
        {
            var obligation = account.GetObligation(byObligation.Key);
            var plan = obligation.ActivePlan!;
            foreach (var (target, amount, strategy) in byObligation)
            {
                var before = plan.Instalments.Single(i => i.Sequence == target.InstalmentSequence);
                plan = plan.Apply(target.InstalmentSequence, target.Component, amount, now);
                var after = plan.Instalments.Single(i => i.Sequence == target.InstalmentSequence);
                if (before.Outstanding.IsPositive && !after.Outstanding.IsPositive)
                {
                    settled.Add(new InstalmentRef(obligation.Id, target.InstalmentSequence));
                }

                records.Add(new AllocationRecord(
                    new AllocationId(Guid.CreateVersion7(time.GetUtcNow())), lot.PaymentId, account.Id, obligation.Id, plan.Version,
                    target.InstalmentSequence, target.DueDate, target.Component, amount, strategy, policyVersion, journalTxId, now));
                total += amount;
            }

            var updated = obligation.WithActivePlan(plan);
            if (updated.Status == ObligationStatus.Settled && obligation.Status != ObligationStatus.Settled)
            {
                obligationsSettled.Add(obligation.Id);
            }

            account = account.ReplaceObligation(updated);
        }

        account = account.ReplaceLot(lot with { Remaining = lot.Remaining - total });
        return new LotAllocation(account, records.ToImmutable(), settled.ToImmutable(), obligationsSettled.ToImmutable());
    }

    private static IEnumerable<(AllocationTarget Target, Instalment Instalment)> OpenTargets(
        IEnumerable<Obligation> obligations, DateOnly asOf, IReadOnlyList<ComponentType> componentOrder)
    {
        foreach (var obligation in obligations)
        {
            var plan = obligation.ActivePlan!;
            var currentDue = plan.Instalments.Where(i => i.DueDate <= asOf).Select(i => (DateOnly?)i.DueDate).Max();
            foreach (var instalment in plan.Instalments.Where(i => i.Outstanding.IsPositive))
            {
                foreach (var component in instalment.Components.Where(c => c.Outstanding.IsPositive))
                {
                    yield return (new AllocationTarget(
                        obligation.Id,
                        obligation.OpenedAt,
                        instalment.Sequence,
                        instalment.DueDate,
                        IsCurrent: instalment.DueDate == currentDue,
                        component.Key,
                        component.Outstanding), instalment);
                }
            }
        }
    }
}
