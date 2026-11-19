namespace InstalmentFlow.Tests;

public sealed class MoneyTests
{
    // Decimal test data must be written as decimal literals. Attribute arguments such as [InlineData(10.01)]
    // are doubles, and the runtime's double-to-decimal conversion is not exact: .NET 11 turns 10.01 into
    // 10.009999999999999786837179272. TestConventionsTests enforces this.
    public static TheoryData<decimal, string> Formats => new()
    {
        { 1500m, "KES 1500.00" },
        { 0.5m, "KES 0.50" },
        { -20.25m, "KES -20.25" },
    };

    public static TheoryData<decimal, string> TooPrecise => new()
    {
        { 1.005m, "KES" },
        { 0.001m, "KES" },
        { 1.5m, "UGX" },
    };

    public static TheoryData<decimal, decimal, MidpointRounding, decimal> Products => new()
    {
        { 10.01m, 0.5m, MidpointRounding.ToEven, 5.00m },        // 5.005 -> banker's rounding
        { 10.01m, 0.5m, MidpointRounding.AwayFromZero, 5.01m },
        { 10.01m, 0.5m, MidpointRounding.ToZero, 5.00m },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void ToString_is_invariant_and_shows_minor_units(decimal amount, string expected)
    {
        Assert.Equal(expected, Money.Kes(amount).ToString());
    }

    [Fact]
    public void ToString_respects_currencies_without_minor_units()
    {
        Assert.Equal("UGX 5000", new Money(5000m, Currency.UGX).ToString());
    }

    [Theory]
    [MemberData(nameof(TooPrecise))]
    public void Rejects_amounts_finer_than_the_currency_allows(decimal amount, string code)
    {
        var ex = Assert.Throws<InvalidAmountException>(() => Money.Of(amount, code));

        Assert.Equal("IF1003", ex.ErrorCode);
    }

    [Fact]
    public void Accepts_trailing_zeros_and_compares_by_value()
    {
        Assert.Equal(Money.Kes(1.5m), Money.Kes(1.500m));
        Assert.Equal(Money.Kes(1.5m).GetHashCode(), Money.Kes(1.500m).GetHashCode());
        Assert.Equal(1.5m, Money.Kes(1.500m).Amount);
    }

    [Fact]
    public void Arithmetic_within_one_currency()
    {
        var a = Money.Kes(500m);
        var b = Money.Kes(150.25m);

        Assert.Equal(Money.Kes(650.25m), a + b);
        Assert.Equal(Money.Kes(349.75m), a - b);
        Assert.Equal(Money.Kes(-500m), -a);
        Assert.Equal(Money.Kes(500m), (-a).Abs());
        Assert.True((a - a).IsZero);
        Assert.True((b - a).IsNegative);
        Assert.Equal(Money.Kes(150.25m), Money.Min(a, b));
        Assert.Equal(Money.Kes(500m), Money.Max(a, b));
        Assert.Equal(15025m, b.MinorUnitAmount);
    }

    [Fact]
    public void Mixing_currencies_throws_IF1001()
    {
        var kes = Money.Kes(100m);
        var ugx = new Money(100m, Currency.UGX);

        Assert.Equal("IF1001", Assert.Throws<CurrencyMismatchException>(() => kes + ugx).ErrorCode);
        Assert.Throws<CurrencyMismatchException>(() => kes - ugx);
        Assert.Throws<CurrencyMismatchException>(() => kes < ugx);
        Assert.Throws<CurrencyMismatchException>(() => Money.Sum(Currency.KES, [kes, ugx]));
        Assert.NotEqual(kes, ugx);
    }

    [Fact]
    public void Comparison_operators()
    {
        Assert.True(Money.Kes(1m) < Money.Kes(2m));
        Assert.True(Money.Kes(2m) > Money.Kes(1m));
        Assert.True(Money.Kes(2m) <= Money.Kes(2m));
        Assert.True(Money.Kes(2m) >= Money.Kes(2m));
        Assert.Equal(1, Money.Kes(0m).CompareTo(null));
    }

    [Fact]
    public void Sum_of_nothing_is_zero_in_the_requested_currency()
    {
        Assert.Equal(Money.Zero(Currency.KES), Money.Sum(Currency.KES, []));
        Assert.Equal(Money.Kes(500m), Money.Sum(Currency.KES, [Money.Kes(100m), Money.Kes(150m), Money.Kes(50m), Money.Kes(200m)]));
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void Multiply_rounds_with_the_explicit_mode(decimal amount, decimal factor, MidpointRounding mode, decimal expected)
    {
        Assert.Equal(Money.Kes(expected), Money.Kes(amount).Multiply(factor, mode));
    }

    [Fact]
    public void Round_creates_exact_money_from_finer_values()
    {
        Assert.Equal(Money.Kes(33.33m), Money.Round(33.3333m, Currency.KES, MidpointRounding.ToEven));
        Assert.Equal(new Money(34m, Currency.UGX), Money.Round(33.5m, Currency.UGX, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Of_parses_the_currency_code()
    {
        Assert.Equal(Money.Kes(500m), Money.Of(500m, "KES"));
    }
}
