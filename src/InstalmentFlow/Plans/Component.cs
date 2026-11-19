using System.Text.RegularExpressions;

namespace InstalmentFlow;

/// <summary>The kind of amount owed. The set is closed; fees and penalties can be distinguished by charge code.</summary>
public enum ComponentType
{
    /// <summary>The amount financed or lent.</summary>
    Principal = 1,

    /// <summary>Interest, as calculated by the host (ADR-008).</summary>
    Interest = 2,

    /// <summary>A fee, for example processing or insurance.</summary>
    Fee = 3,

    /// <summary>A penalty, for example a late-payment charge.</summary>
    Penalty = 4,
}

/// <summary>
/// Identifies one balance within an obligation: a component type plus an optional charge code
/// (for example <c>Fee</c> + <c>insurance</c>). Charge codes allow allocation rules today and distribution
/// rules later (ADR-012). Principal and interest never carry a charge code.
/// </summary>
public sealed partial record ComponentKey : IComparable<ComponentKey>
{
    /// <summary>Maximum charge code length.</summary>
    public const int MaxChargeCodeLength = 32;

    /// <summary>Creates a key.</summary>
    /// <exception cref="ArgumentException">The charge code is invalid, or given for principal or interest.</exception>
    public ComponentKey(ComponentType type, string? chargeCode = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        chargeCode ??= string.Empty;
        if (chargeCode.Length > 0 && type is ComponentType.Principal or ComponentType.Interest)
        {
            throw new ArgumentException("Only fees and penalties can have a charge code.", nameof(chargeCode));
        }

        if (chargeCode.Length > 0 && !ChargeCodePattern().IsMatch(chargeCode))
        {
            throw new ArgumentException("Charge codes are 1-32 lower-case letters, digits, '.', '_' or '-'.", nameof(chargeCode));
        }

        Type = type;
        ChargeCode = chargeCode;
    }

    /// <summary>The principal balance.</summary>
    public static ComponentKey Principal { get; } = new(ComponentType.Principal);

    /// <summary>The interest balance.</summary>
    public static ComponentKey Interest { get; } = new(ComponentType.Interest);

    /// <summary>The component type.</summary>
    public ComponentType Type { get; }

    /// <summary>The charge code, or an empty string.</summary>
    public string ChargeCode { get; }

    /// <summary>Orders by type, then charge code (ordinal).</summary>
    public int CompareTo(ComponentKey? other) =>
        other is null ? 1 : Type != other.Type ? Type.CompareTo(other.Type) : string.CompareOrdinal(ChargeCode, other.ChargeCode);

    /// <summary>Formats as <c>Fee:insurance</c> or <c>Principal</c>.</summary>
    public override string ToString() => ChargeCode.Length == 0 ? Type.ToString() : $"{Type}:{ChargeCode}";

    /// <summary>Compares two keys.</summary>
    public static bool operator <(ComponentKey left, ComponentKey right) => Compare(left, right) < 0;

    /// <summary>Compares two keys.</summary>
    public static bool operator >(ComponentKey left, ComponentKey right) => Compare(left, right) > 0;

    /// <summary>Compares two keys.</summary>
    public static bool operator <=(ComponentKey left, ComponentKey right) => Compare(left, right) <= 0;

    /// <summary>Compares two keys.</summary>
    public static bool operator >=(ComponentKey left, ComponentKey right) => Compare(left, right) >= 0;

    private static int Compare(ComponentKey left, ComponentKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChargeCodePattern();
}

/// <summary>An amount of one component, for example principal KES 15,000.</summary>
public sealed record Component
{
    /// <summary>Creates a component amount. Amounts must be positive.</summary>
    public Component(ComponentKey key, Money amount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(amount);
        if (!amount.IsPositive)
        {
            throw new InvalidAmountException($"Component amounts must be positive; got {amount} for {key}.");
        }

        Key = key;
        Amount = amount;
    }

    /// <summary>Which balance.</summary>
    public ComponentKey Key { get; }

    /// <summary>The amount.</summary>
    public Money Amount { get; }

    /// <summary>A principal amount.</summary>
    public static Component Principal(Money amount) => new(ComponentKey.Principal, amount);

    /// <summary>An interest amount calculated by the host.</summary>
    public static Component Interest(Money amount) => new(ComponentKey.Interest, amount);

    /// <summary>A fee, optionally with a charge code such as <c>insurance</c>.</summary>
    public static Component Fee(Money amount, string? chargeCode = null) => new(new ComponentKey(ComponentType.Fee, chargeCode), amount);

    /// <summary>A penalty, optionally with a charge code.</summary>
    public static Component Penalty(Money amount, string? chargeCode = null) => new(new ComponentKey(ComponentType.Penalty, chargeCode), amount);

    /// <inheritdoc />
    public override string ToString() => $"{Key} {Amount}";
}
