using System.Collections.Immutable;

namespace InstalmentFlow.Domain;

/// <summary>
/// One component of one instalment. <see cref="Due"/> includes host-posted charges, of which <see cref="Charged"/>
/// is the part; <see cref="Allocated"/>, <see cref="Waived"/> and <see cref="Charged"/> are projections of facts.
/// </summary>
internal sealed record InstalmentComponent(ComponentKey Key, Money Due, Money Allocated, Money Waived, Money Charged)
{
    public Money Outstanding => Due - Allocated - Waived;

    public static InstalmentComponent Create(Component component) =>
        new(component.Key, component.Amount, Money.Zero(component.Amount.Currency), Money.Zero(component.Amount.Currency), Money.Zero(component.Amount.Currency));

    /// <summary>A component that exists on the instalment only because a charge was posted to it.</summary>
    public static InstalmentComponent Empty(ComponentKey key, Currency currency) =>
        new(key, Money.Zero(currency), Money.Zero(currency), Money.Zero(currency), Money.Zero(currency));
}

/// <summary>A numbered instalment of a plan version. <see cref="SettledAt"/> is when it was last fully covered.</summary>
internal sealed record Instalment(int Sequence, DateOnly DueDate, ImmutableArray<InstalmentComponent> Components, DateTimeOffset? SettledAt = null)
{
    public Currency Currency => Components[0].Due.Currency;

    public Money Due => Money.Sum(Currency, Components.Select(c => c.Due));

    public Money Allocated => Money.Sum(Currency, Components.Select(c => c.Allocated));

    public Money Waived => Money.Sum(Currency, Components.Select(c => c.Waived));

    public Money Outstanding => Money.Sum(Currency, Components.Select(c => c.Outstanding));

    public bool HasFinancialHistory => Components.Any(c => c.Allocated.IsPositive || c.Waived.IsPositive || c.Charged.IsPositive);
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
    /// <summary>Invariant I-23: a plan with allocations, waivers or charges can never be superseded.</summary>
    public bool HasFinancialHistory => Instalments.Any(i => i.HasFinancialHistory);

    public PlanVersion End(PlanVersionStatus status, DateTimeOffset at, string reason) =>
        this with { Status = status, EndedAt = at, EndReason = reason };

    /// <summary>Applies an allocated amount to one component of one instalment. Enforces I-6: nothing goes below zero.</summary>
    public PlanVersion Apply(int sequence, ComponentKey key, Money amount, DateTimeOffset at) =>
        Change(sequence, key, c => c with { Allocated = c.Allocated + amount }, at, $"Allocating {amount}");

    /// <summary>Undoes part of an allocation (an allocation reversal). The instalment re-opens if it was covered.</summary>
    public PlanVersion Unapply(int sequence, ComponentKey key, Money amount, DateTimeOffset at) =>
        Change(sequence, key, c => c.Allocated >= amount ? c with { Allocated = c.Allocated - amount }
            : throw new InvalidOperationException($"Cannot unwind {amount} from instalment {sequence} {key}; only {c.Allocated} is allocated."), at, $"Unwinding {amount}");

    /// <summary>Waives or writes off part of one component of one instalment.</summary>
    public PlanVersion Waive(int sequence, ComponentKey key, Money amount, DateTimeOffset at) =>
        Change(sequence, key, c => c with { Waived = c.Waived + amount }, at, $"Waiving {amount}");

    /// <summary>Adds a host-posted charge to an instalment, creating the component if the instalment has none of it.</summary>
    public PlanVersion AddCharge(int sequence, ComponentKey key, Money amount, DateTimeOffset at)
    {
        var instalment = Instalments.Single(i => i.Sequence == sequence);
        var plan = this;
        if (instalment.Components.All(c => c.Key != key))
        {
            var added = instalment with { Components = [.. instalment.Components.Add(InstalmentComponent.Empty(key, amount.Currency)).OrderBy(c => c.Key)] };
            plan = this with { Instalments = Instalments.SetItem(Instalments.IndexOf(instalment), added) };
        }

        return plan.Change(sequence, key, c => c with { Due = c.Due + amount, Charged = c.Charged + amount }, at, $"Charging {amount}");
    }

    /// <summary>Removes a reversed charge. The caller first unwinds allocations so the amount is outstanding.</summary>
    public PlanVersion RemoveCharge(int sequence, ComponentKey key, Money amount, DateTimeOffset at) =>
        Change(sequence, key, c => c.Charged >= amount ? c with { Due = c.Due - amount, Charged = c.Charged - amount }
            : throw new InvalidOperationException($"Instalment {sequence} {key} carries only {c.Charged} of charges."), at, $"Reversing a charge of {amount}");

    public Instalment GetInstalment(int sequence) => Instalments.Single(i => i.Sequence == sequence);

    /// <summary>
    /// Changes one component and keeps <see cref="Instalment.SettledAt"/> in step: set when the instalment becomes
    /// fully covered, cleared when it re-opens. Enforces I-6: nothing goes below zero.
    /// </summary>
    private PlanVersion Change(int sequence, ComponentKey key, Func<InstalmentComponent, InstalmentComponent> change, DateTimeOffset at, string what)
    {
        var index = Instalments.IndexOf(Instalments.Single(i => i.Sequence == sequence));
        var instalment = Instalments[index];
        var componentIndex = instalment.Components.IndexOf(instalment.Components.Single(c => c.Key == key));
        var updated = change(instalment.Components[componentIndex]);
        if (updated.Outstanding.IsNegative)
        {
            throw new InvalidOperationException($"{what} on instalment {sequence} {key} would over-settle it (I-6).");
        }

        var next = instalment with { Components = instalment.Components.SetItem(componentIndex, updated) };
        if (next.Outstanding.IsPositive)
        {
            next = next with { SettledAt = null };
        }
        else if (instalment.Outstanding.IsPositive)
        {
            next = next with { SettledAt = at };
        }

        return this with { Instalments = Instalments.SetItem(index, next) };
    }
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
