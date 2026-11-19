using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace InstalmentFlow;

/// <summary>
/// An ISO 4217 currency and the number of minor units (decimal places) amounts may use.
/// Instances are canonical: <see cref="FromCode"/> always returns the same instance for a code.
/// </summary>
/// <remarks>
/// Minor units follow ISO 4217. KES has 2 even though M-Pesa transacts in whole shillings;
/// provider adapters enforce provider-specific precision.
/// </remarks>
public sealed class Currency : IEquatable<Currency>
{
    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>The three-letter ISO 4217 code, upper case.</summary>
    public string Code { get; }

    /// <summary>The number of decimal places amounts in this currency may have.</summary>
    public int MinorUnits { get; }

    /// <summary>Kenyan shilling.</summary>
    public static Currency KES { get; } = new("KES", 2);

    /// <summary>Ugandan shilling (no minor units).</summary>
    public static Currency UGX { get; } = new("UGX", 0);

    /// <summary>Tanzanian shilling.</summary>
    public static Currency TZS { get; } = new("TZS", 2);

    /// <summary>Rwandan franc (no minor units).</summary>
    public static Currency RWF { get; } = new("RWF", 0);

    /// <summary>Burundian franc (no minor units).</summary>
    public static Currency BIF { get; } = new("BIF", 0);

    /// <summary>Ethiopian birr.</summary>
    public static Currency ETB { get; } = new("ETB", 2);

    /// <summary>Zambian kwacha.</summary>
    public static Currency ZMW { get; } = new("ZMW", 2);

    /// <summary>Malawian kwacha.</summary>
    public static Currency MWK { get; } = new("MWK", 2);

    /// <summary>Nigerian naira.</summary>
    public static Currency NGN { get; } = new("NGN", 2);

    /// <summary>Ghanaian cedi.</summary>
    public static Currency GHS { get; } = new("GHS", 2);

    /// <summary>South African rand.</summary>
    public static Currency ZAR { get; } = new("ZAR", 2);

    /// <summary>West African CFA franc (no minor units).</summary>
    public static Currency XOF { get; } = new("XOF", 0);

    /// <summary>Central African CFA franc (no minor units).</summary>
    public static Currency XAF { get; } = new("XAF", 0);

    /// <summary>Congolese franc.</summary>
    public static Currency CDF { get; } = new("CDF", 2);

    /// <summary>Mozambican metical.</summary>
    public static Currency MZN { get; } = new("MZN", 2);

    /// <summary>Egyptian pound.</summary>
    public static Currency EGP { get; } = new("EGP", 2);

    /// <summary>US dollar.</summary>
    public static Currency USD { get; } = new("USD", 2);

    /// <summary>Euro.</summary>
    public static Currency EUR { get; } = new("EUR", 2);

    /// <summary>Pound sterling.</summary>
    public static Currency GBP { get; } = new("GBP", 2);

    private static readonly FrozenDictionary<string, Currency> s_byCode = new[]
    {
        KES, UGX, TZS, RWF, BIF, ETB, ZMW, MWK, NGN, GHS, ZAR, XOF, XAF, CDF, MZN, EGP, USD, EUR, GBP,
    }.ToFrozenDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>All currencies InstalmentFlow recognises.</summary>
    public static IReadOnlyCollection<Currency> All => s_byCode.Values;

    /// <summary>Returns the canonical currency for an ISO 4217 code (case-insensitive).</summary>
    /// <exception cref="UnknownCurrencyException">The code is not recognised.</exception>
    public static Currency FromCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return TryFromCode(code, out var currency) ? currency : throw new UnknownCurrencyException(code);
    }

    /// <summary>Tries to find the canonical currency for an ISO 4217 code (case-insensitive).</summary>
    public static bool TryFromCode(string? code, [NotNullWhen(true)] out Currency? currency)
    {
        currency = null;
        return code is not null && s_byCode.TryGetValue(code.Trim(), out currency);
    }

    /// <inheritdoc />
    public bool Equals(Currency? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Currency);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    /// <summary>Returns the ISO code.</summary>
    public override string ToString() => Code;

    /// <summary>Equality by currency code.</summary>
    public static bool operator ==(Currency? left, Currency? right) => left?.Equals(right) ?? right is null;

    /// <summary>Inequality by currency code.</summary>
    public static bool operator !=(Currency? left, Currency? right) => !(left == right);
}
