namespace InstalmentFlow.Tests;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("KES", 2)]
    [InlineData("kes", 2)]
    [InlineData(" KES ", 2)]
    [InlineData("UGX", 0)]
    [InlineData("RWF", 0)]
    [InlineData("XOF", 0)]
    [InlineData("USD", 2)]
    public void FromCode_returns_canonical_currency_with_iso_minor_units(string code, int minorUnits)
    {
        var currency = Currency.FromCode(code);

        Assert.Equal(code.Trim().ToUpperInvariant(), currency.Code);
        Assert.Equal(minorUnits, currency.MinorUnits);
        Assert.Same(Currency.FromCode(code.Trim().ToUpperInvariant()), currency);
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData("KESX")]
    public void FromCode_rejects_unknown_codes_with_stable_error_code(string code)
    {
        var ex = Assert.Throws<UnknownCurrencyException>(() => Currency.FromCode(code));

        Assert.Equal("IF1004", ex.ErrorCode);
    }

    [Fact]
    public void TryFromCode_handles_null_and_unknown_without_throwing()
    {
        Assert.False(Currency.TryFromCode(null, out _));
        Assert.False(Currency.TryFromCode("ABC", out _));
        Assert.True(Currency.TryFromCode("tzs", out var tzs));
        Assert.Equal(Currency.TZS, tzs);
    }

    [Fact]
    public void Equality_is_by_code()
    {
        Assert.Equal(Currency.KES, Currency.FromCode("KES"));
        Assert.True(Currency.KES == Currency.FromCode("kes"));
        Assert.True(Currency.KES != Currency.UGX);
        Assert.Equal("KES", Currency.KES.ToString());
    }

    [Fact]
    public void Codes_in_the_registry_are_unique_three_letter_upper_case()
    {
        Assert.All(Currency.All, c => Assert.Matches("^[A-Z]{3}$", c.Code));
        Assert.Equal(Currency.All.Count, Currency.All.Select(c => c.Code).Distinct().Count());
    }
}
