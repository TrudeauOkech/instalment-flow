using System.Text.RegularExpressions;

namespace InstalmentFlow;

/// <summary>Names an allocation strategy and the version of its behaviour. Recorded on every allocation.</summary>
public sealed partial record StrategyRef
{
    /// <summary>Creates a reference. Names are 1-64 lower-case letters, digits, '.', '_' or '-'; versions start at 1.</summary>
    public StrategyRef(string name, int version)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException("Strategy names are 1-64 lower-case letters, digits, '.', '_' or '-'.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Name = name;
        Version = version;
    }

    /// <summary>The strategy name, for example <c>oldest-due-first</c>.</summary>
    public string Name { get; }

    /// <summary>The behaviour version.</summary>
    public int Version { get; }

    /// <summary>Formats as <c>name@version</c>.</summary>
    public override string ToString() => $"{Name}@{Version}";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>What happens to funds left after every due instalment is paid.</summary>
public enum FutureInstalmentPolicy
{
    /// <summary>Keep them as unapplied funds. Safest legally; apply later with <see cref="IAllocations.AllocateAsync"/>.</summary>
    HoldAsCredit = 0,

    /// <summary>Pay upcoming instalments in due order (pay-as-you-go: buys days ahead).</summary>
    ApplyToNextInstalments = 1,

    /// <summary>Pay principal on future instalments from the last one backwards, shortening the term.</summary>
    PrepayPrincipal = 2,
}

/// <summary>One unpaid component of one due instalment that a strategy may pay.</summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="ObligationOpenedAt">When the obligation was opened (a deterministic tie-breaker).</param>
/// <param name="InstalmentSequence">The instalment number.</param>
/// <param name="DueDate">The instalment's due date.</param>
/// <param name="IsCurrent">True for the obligation's most recently due instalment; earlier ones are arrears.</param>
/// <param name="Component">The component.</param>
/// <param name="Outstanding">The amount still owed on this component of this instalment.</param>
public sealed record AllocationTarget(
    ObligationId ObligationId,
    DateTimeOffset ObligationOpenedAt,
    int InstalmentSequence,
    DateOnly DueDate,
    bool IsCurrent,
    ComponentKey Component,
    Money Outstanding);

/// <summary>What a strategy is given: the funds, and the due targets it may pay, in a deterministic default order.</summary>
/// <param name="Amount">The funds to allocate.</param>
/// <param name="AsOf">The business date allocation runs for.</param>
/// <param name="Targets">
/// Unpaid components of instalments due on or before <paramref name="AsOf"/>, for the obligations the funds may be
/// applied to, ordered oldest first (due date, obligation opened, obligation ID, instalment, component order).
/// </param>
/// <param name="ComponentOrder">The tenant's component order within an instalment.</param>
public sealed record AllocationContext(Money Amount, DateOnly AsOf, IReadOnlyList<AllocationTarget> Targets, IReadOnlyList<ComponentType> ComponentOrder)
{
    /// <summary>The position of a component type in the tenant's order.</summary>
    public int RankOf(ComponentType type)
    {
        for (var i = 0; i < ComponentOrder.Count; i++)
        {
            if (ComponentOrder[i] == type)
            {
                return i;
            }
        }

        return ComponentOrder.Count;
    }
}

/// <summary>One line of an allocation plan.</summary>
/// <param name="Target">The target to pay; must be one of the context's targets.</param>
/// <param name="Amount">The amount, positive and at most the target's outstanding amount.</param>
public sealed record AllocationLine(AllocationTarget Target, Money Amount);

/// <summary>A strategy's proposal. The engine validates it before anything is applied.</summary>
/// <param name="Lines">Lines, at most one per target.</param>
public sealed record AllocationPlan(IReadOnlyList<AllocationLine> Lines)
{
    /// <summary>A plan that allocates nothing.</summary>
    public static AllocationPlan Empty { get; } = new([]);

    /// <summary>Pays targets in the given order, each as fully as the remaining funds allow.</summary>
    public static AllocationPlan FillInOrder(AllocationContext context, IEnumerable<AllocationTarget> order)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(order);
        var remaining = context.Amount;
        var lines = new List<AllocationLine>();
        foreach (var target in order)
        {
            if (!remaining.IsPositive)
            {
                break;
            }

            var amount = Money.Min(remaining, target.Outstanding);
            if (amount.IsPositive)
            {
                lines.Add(new AllocationLine(target, amount));
                remaining -= amount;
            }
        }

        return new AllocationPlan(lines);
    }
}

/// <summary>
/// Decides how funds are spread across due targets (ADR-006). Implementations must be pure and deterministic:
/// no I/O, no clock, explicit tie-breakers. The engine validates every plan, so a faulty strategy cannot break an
/// invariant; a strategy that throws while a payment is recorded leaves the funds held (ADR-002).
/// </summary>
public interface IAllocationStrategy
{
    /// <summary>The strategy's name and behaviour version. A behaviour change must ship as a new version.</summary>
    StrategyRef Ref { get; }

    /// <summary>Plans the allocation.</summary>
    AllocationPlan Plan(AllocationContext context);
}

/// <summary>Built-in allocation strategies (doc 02 §9). See <c>docs/allocation-strategies/</c> for worked examples.</summary>
public static class AllocationStrategies
{
    /// <summary>Oldest due instalment first; within an instalment, the tenant's component order. The default.</summary>
    public static IAllocationStrategy OldestDueFirst { get; } = new OldestDueFirstStrategy();

    /// <summary>Each obligation's most recently due instalment first, then arrears oldest first.</summary>
    public static IAllocationStrategy CurrentDueFirst { get; } = new CurrentDueFirstStrategy();

    /// <summary>All due principal (oldest first), then interest, fees and penalties.</summary>
    public static IAllocationStrategy PrincipalFirst { get; } = new ComponentMajorStrategy(
        new StrategyRef("principal-first", 1), [ComponentType.Principal, ComponentType.Interest, ComponentType.Fee, ComponentType.Penalty]);

    /// <summary>All due penalties, then fees, then interest (each oldest first), then principal. Use only where contracts allow it.</summary>
    public static IAllocationStrategy ChargesFirst { get; } = new ComponentMajorStrategy(
        new StrategyRef("charges-first", 1), [ComponentType.Penalty, ComponentType.Fee, ComponentType.Interest, ComponentType.Principal]);

    /// <summary>Splits funds across obligations in proportion to their due amounts (largest remainder), then oldest first within each.</summary>
    public static IAllocationStrategy Proportional { get; } = new ProportionalStrategy();

    /// <summary>The built-in strategies.</summary>
    public static IReadOnlyList<IAllocationStrategy> All { get; } = [OldestDueFirst, CurrentDueFirst, PrincipalFirst, ChargesFirst, Proportional];
}

/// <summary>Allocates a customer's existing unapplied funds.</summary>
public sealed record AllocateFundsRequest : InstalmentFlowRequest
{
    /// <summary>The customer.</summary>
    public required AccountRef Account { get; init; }

    /// <summary>
    /// Apply the funds to this obligation only (customer-directed allocation). Overrides any direction recorded
    /// when the funds were received.
    /// </summary>
    public ObligationRef? Obligation { get; init; }

    /// <summary>Allocate only this payment's funds.</summary>
    public PaymentRef? Payment { get; init; }

    /// <summary>
    /// Allow funds that are not directed to an obligation to be spread across all of the customer's payable
    /// obligations by the strategy, even when the tenant's policy holds such funds. Use only on explicit
    /// instruction; funds with an open exception are never included (I-20).
    /// </summary>
    public bool AllowAcrossObligations { get; init; }

    /// <summary>A registered strategy to use instead of the tenant's.</summary>
    public StrategyRef? Strategy { get; init; }
}

/// <summary>Applies the funds of a payment exception to an obligation, resolving it.</summary>
public sealed record AllocateToObligationRequest : InstalmentFlowRequest
{
    /// <summary>The exception.</summary>
    public required PaymentExceptionId ExceptionId { get; init; }

    /// <summary>The customer's obligation to apply the funds to.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>Who is resolving it.</summary>
    public required string ResolvedBy { get; init; }

    /// <summary>An optional note.</summary>
    public string? Note { get; init; }
}

/// <summary>Identifies one instalment.</summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="Sequence">The instalment number.</param>
public sealed record InstalmentRef(ObligationId ObligationId, int Sequence);

/// <summary>One applied allocation.</summary>
/// <param name="PaymentId">The payment whose funds were used.</param>
/// <param name="ObligationId">The obligation paid.</param>
/// <param name="InstalmentSequence">The instalment paid.</param>
/// <param name="DueDate">That instalment's due date.</param>
/// <param name="Component">The component paid.</param>
/// <param name="Amount">The amount.</param>
/// <param name="Strategy">The strategy that chose this line.</param>
public sealed record AllocationLineView(
    PaymentId PaymentId,
    ObligationId ObligationId,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount,
    StrategyRef Strategy);

/// <summary>The result of allocating funds.</summary>
/// <param name="AccountId">The customer.</param>
/// <param name="Allocated">The total allocated, in the currency of the funds (zero if nothing was).</param>
/// <param name="UnappliedAfter">The customer's unapplied funds per currency afterwards.</param>
/// <param name="Lines">What was paid.</param>
/// <param name="InstalmentsSettled">Instalments that became fully covered.</param>
/// <param name="ObligationsSettled">Obligations that became settled.</param>
public sealed record AllocationResult(
    AccountId AccountId,
    IReadOnlyList<Money> Allocated,
    IReadOnlyList<Money> UnappliedAfter,
    IReadOnlyList<AllocationLineView> Lines,
    IReadOnlyList<InstalmentRef> InstalmentsSettled,
    IReadOnlyList<ObligationId> ObligationsSettled);

/// <summary>Allocation of funds that are already recorded.</summary>
public interface IAllocations
{
    /// <summary>
    /// Allocates a customer's unapplied funds, oldest first. Funds with an open payment exception are skipped:
    /// resolve them through <see cref="IPaymentExceptions"/>. Without <see cref="AllocateFundsRequest.Obligation"/>,
    /// funds go to their recorded direction, or follow the tenant's sole/multiple-obligation policies.
    /// </summary>
    Task<AllocationResult> AllocateAsync(AllocateFundsRequest request, CancellationToken cancellationToken = default);
}
