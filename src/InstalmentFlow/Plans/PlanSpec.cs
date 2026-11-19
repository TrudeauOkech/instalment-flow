namespace InstalmentFlow;

/// <summary>
/// Describes how to build a repayment plan for an obligation: which generator lays out the instalments, and the
/// grace period. The engine validates every generated schedule (invariant I-13: totals per component equal the
/// obligation's amounts), so custom generators cannot produce inconsistent plans.
/// </summary>
public sealed record PlanSpec
{
    private PlanSpec(IScheduleGenerator generator, GracePeriod? grace)
    {
        Generator = generator;
        Grace = grace;
    }

    /// <summary>The generator that lays out the instalments.</summary>
    public IScheduleGenerator Generator { get; }

    /// <summary>The grace period, or <see langword="null"/> to use the tenant's default.</summary>
    public GracePeriod? Grace { get; }

    /// <summary>
    /// Equal instalments: each component is split into <paramref name="count"/> equal amounts, with any rounding
    /// remainder on the last instalment. An optional deposit is taken from principal and falls due on its own date.
    /// </summary>
    /// <example>KES 15,000 with a KES 1,500 deposit over 270 daily instalments gives 270 × KES 50.</example>
    public static PlanSpec Equal(Frequency frequency, int count, DateOnly firstDue, GracePeriod? grace = null, Deposit? deposit = null) =>
        new(new EqualInstalmentsGenerator(frequency, count, firstDue, deposit), grace);

    /// <summary>An explicit list of instalments, for example from the host's own pricing engine.</summary>
    public static PlanSpec Custom(IEnumerable<PlannedInstalment> instalments, GracePeriod? grace = null) =>
        new(new CustomScheduleGenerator(instalments), grace);

    /// <summary>A plan built by a custom generator.</summary>
    public static PlanSpec Using(IScheduleGenerator generator, GracePeriod? grace = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new PlanSpec(generator, grace);
    }
}

/// <summary>An up-front payment taken from principal, due on <see cref="DueDate"/>.</summary>
/// <param name="Amount">The deposit amount. It must be less than the principal.</param>
/// <param name="DueDate">When the deposit falls due; on or before the first regular instalment.</param>
public sealed record Deposit(Money Amount, DateOnly DueDate);

/// <summary>One instalment as laid out by a generator, before the engine numbers and validates it.</summary>
/// <param name="DueDate">The due date.</param>
/// <param name="Amounts">The amount due per component. Each key may appear at most once.</param>
public sealed record PlannedInstalment(DateOnly DueDate, IReadOnlyList<Component> Amounts);

/// <summary>What a generator is given: the obligation's amounts per component.</summary>
/// <param name="Currency">The obligation's currency.</param>
/// <param name="Components">The total owed per component, ordered by key.</param>
public sealed record ScheduleContext(Currency Currency, IReadOnlyList<Component> Components);

/// <summary>
/// Lays out instalments for an obligation. Implementations must be deterministic. The engine numbers the
/// result, applies the tenant's business calendar and validates it.
/// </summary>
public interface IScheduleGenerator
{
    /// <summary>A stable name recorded with each plan version, for example <c>equal</c>.</summary>
    string Name { get; }

    /// <summary>Lays out the instalments.</summary>
    IReadOnlyList<PlannedInstalment> Generate(ScheduleContext context);
}

/// <summary>Moves due dates that fall on non-business days. The default calendar moves nothing.</summary>
public interface IBusinessCalendar
{
    /// <summary>The due date to use for a scheduled date.</summary>
    DateOnly AdjustDueDate(DateOnly scheduledDate);
}

/// <summary>Built-in business calendars.</summary>
public static class BusinessCalendars
{
    /// <summary>A calendar that never moves due dates.</summary>
    public static IBusinessCalendar None { get; } = new NoAdjustmentCalendar();

    private sealed class NoAdjustmentCalendar : IBusinessCalendar
    {
        public DateOnly AdjustDueDate(DateOnly scheduledDate) => scheduledDate;
    }
}
