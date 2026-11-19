using CsCheck;

namespace InstalmentFlow.Tests;

/// <summary>Largest-remainder allocation (doc 02 §3, invariant I-14 determinism; failure scenario 13).</summary>
public sealed class MoneyAllocationTests
{
    private static readonly Gen<Currency> s_currency = Gen.OneOf(Currency.KES, Currency.UGX, Currency.USD, Currency.XOF);

    private static readonly Gen<Money> s_money =
        from currency in s_currency
        from minorUnits in Gen.Long[-10_000_000_000L, 10_000_000_000L]
        select new Money(minorUnits / Scale(currency), currency);

    private static readonly Gen<decimal[]> s_ratios =
        Gen.Int[0, 1_000].Array[1, 25].Where(r => r.Sum() > 0).Select(r => r.Select(x => (decimal)x).ToArray());

    [Fact]
    public void Scenario_13_KES_100_over_three_equal_obligations()
    {
        var shares = Money.Kes(100m).Split(3);

        Assert.Equal([Money.Kes(33.34m), Money.Kes(33.33m), Money.Kes(33.33m)], shares);
    }

    [Fact]
    public void Scenario_13_whole_shilling_currency()
    {
        var shares = new Money(100m, Currency.UGX).Split(3);

        Assert.Equal([new Money(34m, Currency.UGX), new Money(33m, Currency.UGX), new Money(33m, Currency.UGX)], shares);
    }

    [Fact]
    public void Remainder_goes_to_the_largest_fractional_part_not_the_first()
    {
        // 1.00 by 1:2 -> exact 0.333.. and 0.666..; the leftover cent goes to the second share.
        var shares = Money.Kes(1m).Allocate([1m, 2m]);

        Assert.Equal([Money.Kes(0.33m), Money.Kes(0.67m)], shares);
    }

    [Fact]
    public void Zero_ratio_gets_nothing_and_negative_amounts_mirror_positive()
    {
        Assert.Equal([Money.Kes(0m), Money.Kes(10m)], Money.Kes(10m).Allocate([0m, 5m]));
        Assert.Equal([Money.Kes(-33.34m), Money.Kes(-33.33m), Money.Kes(-33.33m)], Money.Kes(-100m).Split(3));
    }

    [Fact]
    public void Invalid_ratios_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Money.Kes(10m).Allocate([]));
        Assert.Throws<ArgumentException>(() => Money.Kes(10m).Allocate([0m, 0m]));
        Assert.Throws<ArgumentException>(() => Money.Kes(10m).Allocate([1m, -1m]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Kes(10m).Split(0));
    }

    [Fact]
    public void Property_shares_always_sum_to_the_amount()
    {
        Gen.Select(s_money, s_ratios).Sample((amount, ratios) =>
            Assert.Equal(amount, Money.Sum(amount.Currency, amount.Allocate(ratios))));
    }

    [Fact]
    public void Property_each_share_is_within_one_minor_unit_of_its_exact_proportion()
    {
        Gen.Select(s_money, s_ratios).Sample((amount, ratios) =>
        {
            var shares = amount.Allocate(ratios);
            var total = ratios.Sum();
            var unit = 1m / Scale(amount.Currency);
            for (var i = 0; i < ratios.Length; i++)
            {
                var exact = amount.Amount * ratios[i] / total;
                Assert.True(Math.Abs(shares[i].Amount - exact) < unit,
                    $"share {i}: {shares[i]} vs exact {exact} for {amount} by [{string.Join(", ", ratios)}]");
            }
        });
    }

    [Fact]
    public void Property_extra_units_go_to_the_largest_remainders_earlier_index_on_ties()
    {
        Gen.Select(s_money, s_ratios).Sample((amount, ratios) =>
        {
            var shares = amount.Allocate(ratios);
            var total = ratios.Sum();
            var scale = Scale(amount.Currency);
            var units = decimal.Abs(amount.Amount) * scale;
            var exact = ratios.Select(r => units * r / total).ToArray();
            var remainder = exact.Select(e => e - decimal.Floor(e)).ToArray();
            var gotExtra = shares.Select((s, i) => decimal.Abs(s.Amount) * scale > decimal.Floor(exact[i])).ToArray();

            for (var i = 0; i < ratios.Length; i++)
            {
                for (var j = 0; j < ratios.Length; j++)
                {
                    if (gotExtra[i] && !gotExtra[j])
                    {
                        Assert.True(remainder[i] > remainder[j] || (remainder[i] == remainder[j] && i < j),
                            $"share {i} (remainder {remainder[i]}) got an extra unit but share {j} (remainder {remainder[j]}) did not");
                    }
                }
            }
        });
    }

    [Fact]
    public void Property_allocation_is_deterministic_and_sign_preserving()
    {
        Gen.Select(s_money, s_ratios).Sample((amount, ratios) =>
        {
            var first = amount.Allocate(ratios);
            Assert.Equal(first, amount.Allocate(ratios));
            Assert.All(first, share => Assert.True(share.IsZero || share.IsNegative == amount.IsNegative));
            Assert.All(first, share => Assert.Equal(amount.Currency, share.Currency));
        });
    }

    [Fact]
    public void Property_split_shares_differ_by_at_most_one_minor_unit_with_extras_first()
    {
        Gen.Select(s_money, Gen.Int[1, 400]).Sample((amount, parts) =>
        {
            var shares = amount.Split(parts).Select(s => Math.Abs(s.Amount)).ToList();
            var unit = 1m / Scale(amount.Currency);
            Assert.True(shares.Max() - shares.Min() <= unit);
            Assert.Equal(shares.OrderByDescending(s => s), shares);
        });
    }

    private static decimal Scale(Currency currency)
    {
        var scale = 1m;
        for (var i = 0; i < currency.MinorUnits; i++)
        {
            scale *= 10m;
        }

        return scale;
    }
}
