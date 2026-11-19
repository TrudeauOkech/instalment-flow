using CsCheck;
using InstalmentFlow.Domain;

namespace InstalmentFlow.Tests;

/// <summary>The ADR-009 decision table, row by row, plus properties I-19/I-20 at the decision level.</summary>
public sealed class IdentificationTests
{
    private static readonly AccountId s_customer = new(Guid.CreateVersion7());
    private static readonly AccountId s_other = new(Guid.CreateVersion7());
    private static readonly TenantPolicy s_default = TenantPolicy.Default;

    [Fact]
    public void Case1_exact_match_directs_to_that_obligation_and_raises_nothing()
    {
        var loan = Loan(s_customer, "LN-1");

        var decision = Decide(new ResolvedTarget.OneObligation(loan));

        Assert.Equal(s_customer, decision.HoldingAccount);
        Assert.Equal(loan.Id, decision.DirectedTo);
        Assert.Null(decision.Exception);
    }

    [Fact]
    public void Case2_sole_obligation_is_held_by_default_and_directed_only_with_AutoAllocate()
    {
        var loan = Loan(s_customer, "LN-1");
        var target = new ResolvedTarget.OneCustomer(s_customer, [loan]);

        var held = Decide(target);
        var auto = Decide(target, s_default with { SoleObligation = SoleObligationPolicy.AutoAllocate });

        Assert.Equal((s_customer, (ObligationId?)null, PaymentExceptionReason.AllocationInstructionRequired), (held.HoldingAccount!.Value, held.DirectedTo, held.Exception!.Value));
        Assert.Equal((s_customer, (ObligationId?)loan.Id), (auto.HoldingAccount!.Value, auto.DirectedTo));
        Assert.Null(auto.Exception);
    }

    [Fact]
    public void Case3_several_obligations_are_held_unless_the_tenant_opts_in()
    {
        var target = new ResolvedTarget.OneCustomer(s_customer, [Loan(s_customer, "LN-1"), Loan(s_customer, "LN-2")]);

        var held = Decide(target);
        var optIn = Decide(target, s_default with { MultipleObligations = MultipleObligationsPolicy.AllocateByStrategy });

        Assert.Equal(PaymentExceptionReason.AllocationInstructionRequired, held.Exception);
        Assert.Null(held.DirectedTo);
        Assert.Equal(2, held.Candidates.Length);
        Assert.Null(optIn.Exception);
        Assert.Null(optIn.DirectedTo);
        Assert.Equal(2, optIn.StrategyEligible.Length);
    }

    [Fact]
    public void Case4_no_match_goes_to_unidentified_funds()
    {
        var decision = Decide(new ResolvedTarget.NoMatch("nothing"));

        Assert.Null(decision.HoldingAccount);
        Assert.Equal(PaymentExceptionReason.UnidentifiedPayment, decision.Exception);
    }

    [Fact]
    public void Case5_ambiguity_across_customers_is_unidentified_and_within_one_customer_is_held_by_them()
    {
        var across = Decide(new ResolvedTarget.Several([new(s_customer, null, null), new(s_other, null, null)]));
        var within = Decide(new ResolvedTarget.Several([new(s_customer, new ObligationId(Guid.CreateVersion7()), "LN-1"), new(s_customer, new ObligationId(Guid.CreateVersion7()), "LN-2")]));

        Assert.Equal((null, PaymentExceptionReason.AmbiguousReference), (across.HoldingAccount, across.Exception!.Value));
        Assert.Equal((s_customer, PaymentExceptionReason.AmbiguousReference), (within.HoldingAccount!.Value, within.Exception!.Value));
        Assert.Null(within.DirectedTo);
    }

    public static TheoryData<ObligationStatus, bool> NotPayable => new()
    {
        { ObligationStatus.Settled, true },
        { ObligationStatus.Closed, true },
        { ObligationStatus.Cancelled, true },
        { ObligationStatus.Active, false },   // active, but nothing outstanding
    };

    [Theory]
    [MemberData(nameof(NotPayable))]
    public void Case6_obligation_not_payable_is_held_by_its_customer(ObligationStatus status, bool hasOutstanding)
    {
        var loan = Loan(s_customer, "LN-1") with { Status = status, HasOutstanding = hasOutstanding };

        var decision = Decide(new ResolvedTarget.OneObligation(loan));

        Assert.Equal(s_customer, decision.HoldingAccount);
        Assert.Null(decision.DirectedTo);
        Assert.Equal(PaymentExceptionReason.ObligationNotPayable, decision.Exception);
    }

    [Fact]
    public void Case7_currency_mismatch_is_unidentified()
    {
        var decision = Decide(new ResolvedTarget.OneObligation(Loan(s_customer, "LN-1")), paymentCurrency: Currency.USD);

        Assert.Null(decision.HoldingAccount);
        Assert.Equal(PaymentExceptionReason.CurrencyMismatch, decision.Exception);
    }

    [Fact]
    public void Known_customer_with_nothing_payable_is_held_with_an_exception()
    {
        var decision = Decide(new ResolvedTarget.OneCustomer(s_customer, []));

        Assert.Equal(s_customer, decision.HoldingAccount);
        Assert.Equal(PaymentExceptionReason.NoPayableObligation, decision.Exception);
    }

    [Fact]
    public void Property_every_decision_either_directs_without_exception_or_explains_why_not_I20()
    {
        var loanGen =
            from owner in Gen.OneOf(s_customer, s_other)
            from status in Gen.Enum<ObligationStatus>()
            from outstanding in Gen.Bool
            from usd in Gen.Bool
            select Loan(owner, "LN-x") with { Status = status, HasOutstanding = outstanding, Currency = usd ? Currency.USD : Currency.KES };

        var targetGen = Gen.OneOf<ResolvedTarget>(
            Gen.Const<ResolvedTarget>(new ResolvedTarget.NoMatch("x")),
            loanGen.Select(l => (ResolvedTarget)new ResolvedTarget.OneObligation(l)),
            loanGen.Array[0, 4].Select(ls => (ResolvedTarget)new ResolvedTarget.OneCustomer(s_customer, [.. ls.Select(l => l with { AccountId = s_customer })])),
            loanGen.Array[2, 4].Select(ls => (ResolvedTarget)new ResolvedTarget.Several([.. ls.Select(l => new CandidateView(l.AccountId, l.Id, l.PaymentReference))])));

        var policyGen =
            from sole in Gen.Enum<SoleObligationPolicy>()
            from multiple in Gen.Enum<MultipleObligationsPolicy>()
            select s_default with { SoleObligation = sole, MultipleObligations = multiple };

        Gen.Select(targetGen, policyGen, Gen.OneOf(Currency.KES, Currency.USD)).Sample((target, policy, currency) =>
        {
            var decision = PaymentIdentifier.Decide(target, currency, Currency.KES, policy);

            // Directed funds never carry an exception, and anything not directed or strategy-eligible is explained.
            Assert.False(decision.DirectedTo is not null && decision.Exception is not null);
            Assert.True(decision.DirectedTo is not null || !decision.StrategyEligible.IsEmpty || decision.Exception is not null);

            // Ambiguous and unidentified payments are never directed (I-20).
            if (target is ResolvedTarget.Several or ResolvedTarget.NoMatch)
            {
                Assert.Null(decision.DirectedTo);
                Assert.True(decision.StrategyEligible.IsEmpty);
            }

            // Unidentified funds always carry an exception (I-22 at decision level).
            Assert.True(decision.HoldingAccount is not null || decision.Exception is not null);
        });
    }

    private static ObligationFacts Loan(AccountId owner, string reference) =>
        new(owner, new ObligationId(Guid.CreateVersion7()), reference, Currency.KES, ObligationStatus.Active, HasOutstanding: true);

    private static IdentificationDecision Decide(ResolvedTarget target, TenantPolicy? policy = null, Currency? paymentCurrency = null) =>
        PaymentIdentifier.Decide(target, paymentCurrency ?? Currency.KES, Currency.KES, policy ?? s_default);
}
