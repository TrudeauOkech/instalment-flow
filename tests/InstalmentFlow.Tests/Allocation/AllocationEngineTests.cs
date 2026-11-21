using CsCheck;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Tests;

/// <summary>Properties of the allocation engine for every built-in strategy and future-instalment policy.</summary>
public sealed class AllocationEngineTests
{
    private static readonly DateTimeOffset s_epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly s_start = new(2026, 12, 1);

    private static readonly Gen<Component[]> s_instalmentAmounts =
        from principal in Gen.Int[0, 500]
        from interest in Gen.Int[0, 60]
        from fee in Gen.Int[0, 20]
        from penalty in Gen.Int[0, 20]
        where principal + interest + fee + penalty > 0
        select new[]
        {
            (ComponentKey.Principal, principal), (ComponentKey.Interest, interest),
            (new ComponentKey(ComponentType.Fee), fee), (new ComponentKey(ComponentType.Penalty), penalty),
        }.Where(c => c.Item2 > 0).Select(c => new Component(c.Item1, Money.Kes(c.Item2))).ToArray();

    private static readonly Gen<(CustomerAccount Account, PaymentId Lot, ObligationId[] Ids)> s_account =
        from obligations in Gen.Select(Gen.Int[0, 10], s_instalmentAmounts.Array[1, 8]).Array[1, 4]
        from pay in Gen.Int[1, 3_000]
        select Build(obligations, pay);

    private static readonly Gen<TenantPolicy> s_policy =
        from strategy in Gen.OneOf(AllocationStrategies.All.ToArray())
        from future in Gen.Enum<FutureInstalmentPolicy>()
        from order in Gen.Shuffle(new[] { ComponentType.Principal, ComponentType.Interest, ComponentType.Fee, ComponentType.Penalty })
        select TenantPolicy.Default with { Allocation = strategy.Ref, FutureInstalments = future, ComponentOrder = order };

    [Fact]
    public void Property_conservation_no_over_settlement_and_eligibility()
    {
        Gen.Select(s_account, s_policy, Gen.Int[0, 12], Gen.Int[1, 4]).Sample((setup, policy, asOfOffset, eligibleCount) =>
        {
            var (account, lotId, ids) = setup;
            var eligible = ids.Take(eligibleCount).ToList();
            var asOf = s_start.AddDays(asOfOffset);
            var lot = account.Lots.Single();

            var result = Allocate(account, lotId, eligible, policy, asOf);
            var allocated = Money.Sum(Currency.KES, result.Allocations.Select(a => a.Amount));
            var after = result.Account;

            // Conservation (I-5): allocated plus what is left equals the funds.
            Assert.Equal(lot.Remaining, allocated + after.Lots.Single().Remaining);
            Assert.All(result.Allocations, a => Assert.True(a.Amount.IsPositive));

            // Eligibility (I-21): nothing outside the permitted obligations.
            Assert.All(result.Allocations, a => Assert.Contains(a.ObligationId, eligible));

            // No over-settlement (I-6), and balances move by exactly the allocations.
            foreach (var obligation in after.Obligations)
            {
                var before = account.GetObligation(obligation.Id);
                foreach (var instalment in obligation.ActivePlan!.Instalments)
                {
                    var original = before.ActivePlan!.Instalments.Single(i => i.Sequence == instalment.Sequence);
                    foreach (var component in instalment.Components)
                    {
                        Assert.False(component.Outstanding.IsNegative);
                        var paid = result.Allocations
                            .Where(a => a.ObligationId == obligation.Id && a.InstalmentSequence == instalment.Sequence && a.Component == component.Key)
                            .Aggregate(Money.Zero(Currency.KES), (sum, a) => sum + a.Amount);
                        Assert.Equal(original.Components.Single(c => c.Key == component.Key).Allocated + paid, component.Allocated);
                    }
                }
            }

            // Hold-as-credit never touches an instalment that is not yet due.
            if (policy.FutureInstalments == FutureInstalmentPolicy.HoldAsCredit)
            {
                Assert.All(result.Allocations, a => Assert.True(a.DueDate <= asOf));
            }

            // Funds are only held back when nothing eligible that the policy may pay is left open.
            if (after.Lots.Single().Remaining.IsPositive)
            {
                var payable = after.Obligations.Where(o => eligible.Contains(o.Id))
                    .SelectMany(o => o.ActivePlan!.Instalments)
                    .Where(i => policy.FutureInstalments == FutureInstalmentPolicy.ApplyToNextInstalments || i.DueDate <= asOf)
                    .Sum(i => i.Outstanding.Amount);
                Assert.Equal(0m, payable);
            }
        });
    }

    [Fact]
    public void Property_allocation_is_deterministic_I14()
    {
        Gen.Select(s_account, s_policy, Gen.Int[0, 12]).Sample((setup, policy, asOfOffset) =>
        {
            var asOf = s_start.AddDays(asOfOffset);
            var first = Allocate(setup.Account, setup.Lot, setup.Ids, policy, asOf);
            var second = Allocate(setup.Account, setup.Lot, setup.Ids, policy, asOf);

            Assert.Equal(Describe(first), Describe(second));
        });
    }

    [Fact]
    public void Property_proportional_shares_are_within_one_cent_of_exact_when_funds_do_not_cover_everything_due()
    {
        var policy = TenantPolicy.Default with { Allocation = AllocationStrategies.Proportional.Ref };
        Gen.Select(s_account, Gen.Int[0, 12]).Sample((setup, asOfOffset) =>
        {
            var asOf = s_start.AddDays(asOfOffset);
            var due = setup.Account.Obligations.ToDictionary(
                o => o.Id,
                o => o.ActivePlan!.Instalments.Where(i => i.DueDate <= asOf).Sum(i => i.Outstanding.Amount));
            var totalDue = due.Values.Sum();
            var funds = setup.Account.Lots.Single().Remaining.Amount;
            if (totalDue == 0m || funds > totalDue)
            {
                return;
            }

            var result = Allocate(setup.Account, setup.Lot, setup.Ids, policy, asOf);
            foreach (var (id, owed) in due)
            {
                var share = result.Allocations.Where(a => a.ObligationId == id).Sum(a => a.Amount.Amount);
                Assert.True(Math.Abs(share - (funds * owed / totalDue)) < 0.01m, $"share {share} vs exact {funds * owed / totalDue}");
            }
        });
    }

    [Fact]
    public void Invalid_plans_are_rejected_IF2002()
    {
        var (account, lot, ids) = Build([(0, [[Component.Principal(Money.Kes(100m))]])], 500);
        var cases = new Func<AllocationContext, AllocationPlan>[]
        {
            ctx => new AllocationPlan([new AllocationLine(ctx.Targets[0], ctx.Targets[0].Outstanding + Money.Kes(1m))]),                 // over-settles
            ctx => new AllocationPlan([new AllocationLine(ctx.Targets[0] with { ObligationId = new ObligationId(Guid.NewGuid()) }, Money.Kes(1m))]), // not permitted
            ctx => new AllocationPlan([new AllocationLine(ctx.Targets[0], Money.Kes(-5m))]),                                             // negative
            ctx => new AllocationPlan([new AllocationLine(ctx.Targets[0], Money.Kes(10m)), new AllocationLine(ctx.Targets[0], Money.Kes(10m))]), // duplicate target
            ctx => new AllocationPlan([new AllocationLine(ctx.Targets[0], Money.Of(1m, "USD"))]),                                        // currency
            _ => null!,
        };

        foreach (var plan in cases)
        {
            var strategy = new LambdaStrategy(plan);
            var ex = Assert.Throws<RuleViolationException>(() => AllocationEngine.Allocate(
                account, lot, ids, strategy, TenantPolicy.Default, 1, s_start, new JournalTxId(Guid.CreateVersion7()), s_epoch, TimeProvider.System));
            Assert.Equal(ErrorCodes.InvalidAllocationPlan, ex.ErrorCode);
        }
    }

    [Fact]
    public void A_plan_that_spends_more_than_the_funds_is_rejected()
    {
        var (account, lot, ids) = Build([(0, [[Component.Principal(Money.Kes(100m)), Component.Interest(Money.Kes(100m))]])], 50);
        var strategy = new LambdaStrategy(ctx => new AllocationPlan([.. ctx.Targets.Select(t => new AllocationLine(t, Money.Kes(40m)))]));

        Assert.Throws<RuleViolationException>(() => AllocationEngine.Allocate(
            account, lot, ids, strategy, TenantPolicy.Default, 1, s_start, new JournalTxId(Guid.CreateVersion7()), s_epoch, TimeProvider.System));
    }

    internal static (CustomerAccount Account, PaymentId Lot, ObligationId[] Ids) Build((int StartOffset, Component[][] Instalments)[] obligations, int pay)
    {
        var accountId = new AccountId(Guid.CreateVersion7());
        var built = new List<Obligation>();
        for (var o = 0; o < obligations.Length; o++)
        {
            var (start, instalments) = obligations[o];
            var planned = instalments.Select((amounts, i) => new PlannedInstalment(s_start.AddDays(start + i), amounts)).ToList();
            var originated = planned.SelectMany(p => p.Amounts).GroupBy(c => c.Key)
                .Select(g => new Component(g.Key, Money.Sum(Currency.KES, g.Select(c => c.Amount)))).ToList();
            var plan = ScheduleBuilder.Build(1, PlanSpec.Custom(planned), originated, Currency.KES, GracePeriod.None, BusinessCalendars.None, s_epoch);
            built.Add(new Obligation(new ObligationId(Guid.CreateVersion7()), accountId, $"loan-{o}", $"LN-{o}", Currency.KES,
                ObligationStatus.Active, [.. originated], [plan], null, s_epoch.AddMinutes(o)));
        }

        var lot = new PaymentId(Guid.CreateVersion7());
        var account = new CustomerAccount("t", accountId, "C", 1, s_epoch, [.. built], [FundsLot.Received(lot, Money.Kes(pay), null, s_epoch)]);
        return (account, lot, [.. built.Select(b => b.Id)]);
    }

    private static LotAllocation Allocate(CustomerAccount account, PaymentId lot, IReadOnlyCollection<ObligationId> eligible, TenantPolicy policy, DateOnly asOf) =>
        AllocationEngine.Allocate(
            account, lot, eligible, AllocationStrategies.All.Single(s => s.Ref == policy.Allocation), policy, 1, asOf,
            new JournalTxId(Guid.CreateVersion7()), s_epoch, TimeProvider.System);

    private static List<string> Describe(LotAllocation result) =>
        [.. result.Allocations.Select(a => $"{a.ObligationId}#{a.InstalmentSequence}:{a.Component}:{a.Amount}:{a.Strategy}")];

    private sealed class LambdaStrategy(Func<AllocationContext, AllocationPlan> plan) : IAllocationStrategy
    {
        public StrategyRef Ref { get; } = new("test.lambda", 1);

        public AllocationPlan Plan(AllocationContext context) => plan(context);
    }
}
