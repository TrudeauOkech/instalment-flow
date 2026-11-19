using System.Globalization;

namespace InstalmentFlow;

/// <summary>
/// An exact monetary amount in one currency. Amounts are <see cref="decimal"/> and may never have
/// more decimal places than the currency's <see cref="Currency.MinorUnits"/>. Arithmetic is only
/// defined between amounts in the same currency; anything else throws <see cref="CurrencyMismatchException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Money"/> is an immutable reference type rather than a struct, so an uninitialised
/// value can never silently appear as an amount with no currency.
/// </para>
/// <para>
/// Amounts may be negative (for example a delta). Commands that move money validate positivity themselves.
/// </para>
/// </remarks>
public sealed class Money : IEquatable<Money>, IComparable<Money>
{
    /// <summary>Creates an amount, which must already be exact in the currency's minor units.</summary>
    /// <exception cref="InvalidAmountException">The amount has more decimal places than the currency allows.</exception>
    public Money(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var exact = decimal.Round(amount, currency.MinorUnits, MidpointRounding.ToZero);
        if (exact != amount)
        {
            throw new InvalidAmountException(
                $"{amount.ToString(CultureInfo.InvariantCulture)} has more than {currency.MinorUnits} decimal place(s), which {currency} does not allow.");
        }

        Amount = exact;
        Currency = currency;
    }

    /// <summary>The amount, with at most <see cref="Currency.MinorUnits"/> decimal places.</summary>
    public decimal Amount { get; }

    /// <summary>The currency of the amount.</summary>
    public Currency Currency { get; }

    /// <summary>True when the amount is zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>True when the amount is greater than zero.</summary>
    public bool IsPositive => Amount > 0m;

    /// <summary>True when the amount is less than zero.</summary>
    public bool IsNegative => Amount < 0m;

    /// <summary>The amount expressed in whole minor units (for example cents), as an integral decimal.</summary>
    public decimal MinorUnitAmount => Amount * Pow10(Currency.MinorUnits);

    /// <summary>Creates an amount from an ISO 4217 currency code.</summary>
    public static Money Of(decimal amount, string currencyCode) => new(amount, Currency.FromCode(currencyCode));

    /// <summary>Creates a Kenyan shilling amount.</summary>
    public static Money Kes(decimal amount) => new(amount, Currency.KES);

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>Creates an amount by rounding to the currency's minor units with an explicit rounding mode.</summary>
    public static Money Round(decimal amount, Currency currency, MidpointRounding mode)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(decimal.Round(amount, currency.MinorUnits, mode), currency);
    }

    /// <summary>Sums amounts that must all be in <paramref name="currency"/>.</summary>
    public static Money Sum(Currency currency, IEnumerable<Money> amounts)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(amounts);
        var total = 0m;
        foreach (var amount in amounts)
        {
            ArgumentNullException.ThrowIfNull(amount);
            RequireCurrency(currency, amount.Currency);
            total += amount.Amount;
        }

        return new Money(total, currency);
    }

    /// <summary>The smaller of two amounts in the same currency.</summary>
    public static Money Min(Money left, Money right) => Compare(left, right) <= 0 ? left : right;

    /// <summary>The larger of two amounts in the same currency.</summary>
    public static Money Max(Money left, Money right) => Compare(left, right) >= 0 ? left : right;

    /// <summary>Adds two amounts in the same currency.</summary>
    public Money Add(Money other)
    {
        RequireSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <summary>Subtracts an amount in the same currency.</summary>
    public Money Subtract(Money other)
    {
        RequireSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>The amount with its sign reversed.</summary>
    public Money Negate() => new(-Amount, Currency);

    /// <summary>The absolute value of the amount.</summary>
    public Money Abs() => IsNegative ? Negate() : this;

    /// <summary>Multiplies by a factor, rounding to the currency's minor units with an explicit mode.</summary>
    public Money Multiply(decimal factor, MidpointRounding mode) => Round(Amount * factor, Currency, mode);

    /// <summary>
    /// Splits the amount into shares proportional to <paramref name="ratios"/> using the largest-remainder
    /// method. The shares always sum exactly to this amount, each share is within one minor unit of its exact
    /// proportional value, and ties are broken deterministically in favour of the earlier ratio.
    /// </summary>
    /// <exception cref="ArgumentException">No ratios, a negative ratio, or ratios summing to zero.</exception>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        if (ratios.Count == 0)
        {
            throw new ArgumentException("At least one ratio is required.", nameof(ratios));
        }

        var ratioTotal = 0m;
        foreach (var ratio in ratios)
        {
            if (ratio < 0m)
            {
                throw new ArgumentException("Ratios must not be negative.", nameof(ratios));
            }

            ratioTotal += ratio;
        }

        if (ratioTotal == 0m)
        {
            throw new ArgumentException("Ratios must not all be zero.", nameof(ratios));
        }

        // Work in whole minor units of the absolute amount; restore the sign at the end.
        var scale = Pow10(Currency.MinorUnits);
        var totalUnits = decimal.Abs(Amount) * scale;
        var units = new decimal[ratios.Count];
        var remainders = new decimal[ratios.Count];
        var allocatedUnits = 0m;

        for (var i = 0; i < ratios.Count; i++)
        {
            var exact = totalUnits * ratios[i] / ratioTotal;
            units[i] = decimal.Floor(exact);
            remainders[i] = exact - units[i];
            allocatedUnits += units[i];
        }

        // Hand out the leftover units, one each, to the largest remainders (earlier index wins ties).
        var leftover = (int)(totalUnits - allocatedUnits);
        if (leftover > 0)
        {
            var order = Enumerable.Range(0, ratios.Count)
                .OrderByDescending(i => remainders[i])
                .ThenBy(i => i)
                .Take(leftover);
            foreach (var i in order)
            {
                units[i] += 1m;
            }
        }

        var sign = IsNegative ? -1m : 1m;
        var shares = new Money[ratios.Count];
        for (var i = 0; i < ratios.Count; i++)
        {
            shares[i] = new Money(sign * units[i] / scale, Currency);
        }

        return shares;
    }

    /// <summary>
    /// Splits the amount into <paramref name="parts"/> shares that differ by at most one minor unit;
    /// any remainder goes to the earliest shares.
    /// </summary>
    public IReadOnlyList<Money> Split(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts);
        return Allocate(Enumerable.Repeat(1m, parts).ToArray());
    }

    /// <inheritdoc />
    public int CompareTo(Money? other)
    {
        if (other is null)
        {
            return 1;
        }

        RequireSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    /// <inheritdoc />
    public bool Equals(Money? other) => other is not null && Currency.Equals(other.Currency) && Amount == other.Amount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Money);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Currency, Amount);

    /// <summary>Formats as <c>"KES 1500.00"</c>, culture-invariant, always showing the currency's minor units.</summary>
    public override string ToString() =>
        $"{Currency.Code} {Amount.ToString("F" + Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}";

    /// <summary>Adds two amounts in the same currency.</summary>
    public static Money operator +(Money left, Money right) => NotNull(left).Add(right);

    /// <summary>Subtracts two amounts in the same currency.</summary>
    public static Money operator -(Money left, Money right) => NotNull(left).Subtract(right);

    /// <summary>Reverses the sign of an amount.</summary>
    public static Money operator -(Money value) => NotNull(value).Negate();

    /// <summary>Equality of currency and amount.</summary>
    public static bool operator ==(Money? left, Money? right) => left?.Equals(right) ?? right is null;

    /// <summary>Inequality of currency or amount.</summary>
    public static bool operator !=(Money? left, Money? right) => !(left == right);

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    private static int Compare(Money left, Money right) => NotNull(left).CompareTo(NotNull(right));

    private static Money NotNull(Money value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }

    private void RequireSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        RequireCurrency(Currency, other.Currency);
    }

    private static void RequireCurrency(Currency expected, Currency actual)
    {
        if (!expected.Equals(actual))
        {
            throw new CurrencyMismatchException(expected, actual);
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
