namespace InstalmentFlow;

/// <summary>Tie-breaking order shared by the built-in strategies (doc 02 §9): due date, obligation opened, obligation ID, instalment.</summary>
internal static class TargetOrder
{
    public static IOrderedEnumerable<AllocationTarget> ByInstalment(IEnumerable<AllocationTarget> targets) =>
        targets
            .OrderBy(t => t.DueDate)
            .ThenBy(t => t.ObligationOpenedAt)
            .ThenBy(t => t.ObligationId.Value)
            .ThenBy(t => t.InstalmentSequence);

    public static IEnumerable<AllocationTarget> InstalmentThenComponent(AllocationContext context, IEnumerable<AllocationTarget> targets) =>
        ByInstalment(targets).ThenBy(t => context.RankOf(t.Component.Type)).ThenBy(t => t.Component.ChargeCode, StringComparer.Ordinal);
}

internal sealed class OldestDueFirstStrategy : IAllocationStrategy
{
    public StrategyRef Ref { get; } = new("oldest-due-first", 1);

    public AllocationPlan Plan(AllocationContext context) =>
        AllocationPlan.FillInOrder(context, TargetOrder.InstalmentThenComponent(context, context.Targets));
}

internal sealed class CurrentDueFirstStrategy : IAllocationStrategy
{
    public StrategyRef Ref { get; } = new("current-due-first", 1);

    public AllocationPlan Plan(AllocationContext context) =>
        AllocationPlan.FillInOrder(context,
            TargetOrder.InstalmentThenComponent(context, context.Targets.Where(t => t.IsCurrent))
                .Concat(TargetOrder.InstalmentThenComponent(context, context.Targets.Where(t => !t.IsCurrent))));
}

/// <summary>Pays one component type across every due instalment before moving to the next type.</summary>
internal sealed class ComponentMajorStrategy(StrategyRef reference, IReadOnlyList<ComponentType> order) : IAllocationStrategy
{
    public StrategyRef Ref { get; } = reference;

    public AllocationPlan Plan(AllocationContext context) =>
        AllocationPlan.FillInOrder(context, order.SelectMany(type =>
            TargetOrder.ByInstalment(context.Targets.Where(t => t.Component.Type == type)).ThenBy(t => t.Component.ChargeCode, StringComparer.Ordinal)));
}

/// <summary>
/// Splits the funds across obligations in proportion to their due outstanding amounts using the largest-remainder
/// method, then pays oldest first within each obligation. A share never exceeds its obligation's due amount.
/// </summary>
internal sealed class ProportionalStrategy : IAllocationStrategy
{
    public StrategyRef Ref { get; } = new("proportional", 1);

    public AllocationPlan Plan(AllocationContext context)
    {
        var byObligation = context.Targets
            .GroupBy(t => t.ObligationId)
            .OrderBy(g => g.Min(t => t.ObligationOpenedAt))
            .ThenBy(g => g.Key.Value)
            .ToList();
        if (byObligation.Count == 0)
        {
            return AllocationPlan.Empty;
        }

        var currency = context.Amount.Currency;
        var dueByObligation = byObligation.Select(g => Money.Sum(currency, g.Select(t => t.Outstanding))).ToList();
        var totalDue = Money.Sum(currency, dueByObligation);
        var shares = Money.Min(context.Amount, totalDue).Allocate([.. dueByObligation.Select(d => d.Amount)]);

        var lines = new List<AllocationLine>();
        for (var i = 0; i < byObligation.Count; i++)
        {
            var share = context with { Amount = shares[i] };
            lines.AddRange(AllocationPlan.FillInOrder(share, TargetOrder.InstalmentThenComponent(context, byObligation[i])).Lines);
        }

        return new AllocationPlan(lines);
    }
}
