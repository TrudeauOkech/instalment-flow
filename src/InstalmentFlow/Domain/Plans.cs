using System.Collections.Immutable;

namespace InstalmentFlow.Domain;

/// <summary>One component of one instalment. <see cref="Allocated"/> and <see cref="Waived"/> are projections.</summary>
internal sealed record InstalmentComponent(ComponentKey Key, Money Due, Money Allocated, Money Waived)
{
    public Money Outstanding => Due - Allocated - Waived;

    public static InstalmentComponent Create(Component component) =>
        new(component.Key, component.Amount, Money.Zero(component.Amount.Currency), Money.Zero(component.Amount.Currency));
}

/// <summary>A numbered instalment of a plan version.</summary>
internal sealed record Instalment(int Sequence, DateOnly DueDate, ImmutableArray<InstalmentComponent> Components)
{
    public Currency Currency => Components[0].Due.Currency;

    public Money Due => Money.Sum(Currency, Components.Select(c => c.Due));

    public Money Allocated => Money.Sum(Currency, Components.Select(c => c.Allocated));

    public Money Waived => Money.Sum(Currency, Components.Select(c => c.Waived));

    public Money Outstanding => Money.Sum(Currency, Components.Select(c => c.Outstanding));

    public bool HasFinancialHistory => Components.Any(c => c.Allocated.IsPositive || c.Waived.IsPositive);
}

/// <summary>One immutable version of an obligation's repayment plan.</summary>
internal sealed record PlanVersion(
    int Version,
    PlanVersionStatus Status,
    GracePeriod Grace,
    string Generator,
    ImmutableArray<Instalment> Instalments,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    string? EndReason)
{
    /// <summary>Invariant I-23: a plan with allocations or waivers can never be superseded.</summary>
    public bool HasFinancialHistory => Instalments.Any(i => i.HasFinancialHistory);

    public PlanVersion End(PlanVersionStatus status, DateTimeOffset at, string reason) =>
        this with { Status = status, EndedAt = at, EndReason = reason };
}

/// <summary>Turns a <see cref="PlanSpec"/> into a validated, numbered plan version.</summary>
internal static class ScheduleBuilder
{
    public static PlanVersion Build(
        int version,
        PlanSpec spec,
        IReadOnlyList<Component> originated,
        Currency currency,
        GracePeriod defaultGrace,
        IBusinessCalendar calendar,
        DateTimeOffset now)
    {
        var context = new ScheduleContext(currency, originated.OrderBy(c => c.Key).ToList());
        var planned = spec.Generator.Generate(context)
            ?? throw Invalid($"Generator '{spec.Generator.Name}' returned no schedule.");

        if (planned.Count == 0)
        {
            throw Invalid("A plan needs at least one instalment.");
        }

        if (planned.Count > EqualInstalmentsGenerator.MaxCount + 1)
        {
            throw Invalid($"A plan may have at most {EqualInstalmentsGenerator.MaxCount + 1} instalments.");
        }

        var instalments = ImmutableArray.CreateBuilder<Instalment>(planned.Count);
        var previousDue = DateOnly.MinValue;
        for (var i = 0; i < planned.Count; i++)
        {
            var item = planned[i] ?? throw Invalid($"Instalment {i + 1} is missing.");
            var due = calendar.AdjustDueDate(item.DueDate);
            if (due < previousDue)
            {
                throw Invalid($"Instalment {i + 1} falls due on {due:yyyy-MM-dd}, before the previous instalment.");
            }

            var components = (item.Amounts ?? [])
                .Select(c => c ?? throw Invalid($"Instalment {i + 1} has a missing component."))
                .ToList();
            if (components.Count == 0)
            {
                throw Invalid($"Instalment {i + 1} has nothing due.");
            }

            if (components.Select(c => c.Key).Distinct().Count() != components.Count)
            {
                throw Invalid($"Instalment {i + 1} lists a component more than once.");
            }

            foreach (var component in components)
            {
                if (component.Amount.Currency != currency)
                {
                    throw new CurrencyMismatchException(currency, component.Amount.Currency);
                }
            }

            instalments.Add(new Instalment(
                i + 1,
                due,
                [.. components.OrderBy(c => c.Key).Select(InstalmentComponent.Create)]));
            previousDue = due;
        }

        RequireTotalsMatch(instalments, originated, currency);

        return new PlanVersion(
            version,
            PlanVersionStatus.Active,
            spec.Grace ?? defaultGrace,
            spec.Generator.Name,
            instalments.MoveToImmutable(),
            now,
            EndedAt: null,
            EndReason: null);
    }

    /// <summary>Invariant I-13: per component, the schedule totals equal the obligation's amounts.</summary>
    private static void RequireTotalsMatch(IEnumerable<Instalment> instalments, IReadOnlyList<Component> originated, Currency currency)
    {
        var scheduled = instalments
            .SelectMany(i => i.Components)
            .GroupBy(c => c.Key)
            .ToDictionary(g => g.Key, g => Money.Sum(currency, g.Select(c => c.Due)));
        var owed = originated.ToDictionary(c => c.Key, c => c.Amount);

        foreach (var key in scheduled.Keys.Union(owed.Keys).Order())
        {
            var inSchedule = scheduled.GetValueOrDefault(key) ?? Money.Zero(currency);
            var inObligation = owed.GetValueOrDefault(key) ?? Money.Zero(currency);
            if (inSchedule != inObligation)
            {
                throw Invalid($"Schedule total for {key} is {inSchedule} but the obligation owes {inObligation} (I-13).");
            }
        }
    }

    private static RuleViolationException Invalid(string message) => new(ErrorCodes.InvalidSchedule, message);
}
