using System.Collections.Immutable;

namespace InstalmentFlow.Domain;

/// <summary>What identification needs to know about an obligation.</summary>
internal sealed record ObligationFacts(AccountId AccountId, ObligationId Id, string PaymentReference, Currency Currency, ObligationStatus Status, bool HasOutstanding)
{
    public bool IsPayableIn(Currency currency) => Status == ObligationStatus.Active && HasOutstanding && Currency == currency;

    public static ObligationFacts From(Obligation obligation) => new(
        obligation.AccountId,
        obligation.Id,
        obligation.PaymentReference,
        obligation.Currency,
        obligation.Status,
        obligation.Plans.Where(p => p.Status == PlanVersionStatus.Active).SelectMany(p => p.Instalments).Any(i => i.Outstanding.IsPositive));
}

/// <summary>The resolved target of a payment, before the decision.</summary>
internal abstract record ResolvedTarget
{
    private ResolvedTarget()
    {
    }

    /// <summary>Nothing matched (case 4).</summary>
    public sealed record NoMatch(string Detail) : ResolvedTarget;

    /// <summary>Exactly one obligation matched, or the host named it.</summary>
    public sealed record OneObligation(ObligationFacts Obligation) : ResolvedTarget;

    /// <summary>A customer matched, but no specific obligation.</summary>
    public sealed record OneCustomer(AccountId AccountId, ImmutableArray<ObligationFacts> Obligations) : ResolvedTarget;

    /// <summary>More than one obligation or customer matched (case 5).</summary>
    public sealed record Several(ImmutableArray<CandidateView> Candidates) : ResolvedTarget;
}

/// <summary>Where the funds are held and what they may be applied to.</summary>
/// <param name="HoldingAccount">The customer holding the funds, or null for tenant unidentified funds.</param>
/// <param name="DirectedTo">The obligation the funds are directed to (cases 1 and 2), applied by allocation in M4.</param>
/// <param name="StrategyEligible">Obligations a strategy may spread the funds across (case 3 with explicit opt-in).</param>
/// <param name="Exception">The exception to raise, if any.</param>
/// <param name="Detail">A human-readable explanation.</param>
/// <param name="Candidates">Candidates, recorded on the exception.</param>
internal sealed record IdentificationDecision(
    AccountId? HoldingAccount,
    ObligationId? DirectedTo,
    ImmutableArray<ObligationId> StrategyEligible,
    PaymentExceptionReason? Exception,
    string Detail,
    ImmutableArray<CandidateView> Candidates);

/// <summary>
/// Payment identification (ADR-009) as a pure function: the same target, currency and policy always give the same
/// decision. Every outcome records the payment (ADR-002); none allocates an ambiguous payment (I-20).
/// </summary>
internal static class PaymentIdentifier
{
    public static IdentificationDecision Decide(ResolvedTarget target, Currency paymentCurrency, Currency tenantCurrency, TenantPolicy policy) =>
        target switch
        {
            ResolvedTarget.NoMatch noMatch => Unidentified(PaymentExceptionReason.UnidentifiedPayment, noMatch.Detail, []),
            ResolvedTarget.OneObligation one => ForObligation(one.Obligation, paymentCurrency),
            ResolvedTarget.OneCustomer customer => ForCustomer(customer, paymentCurrency, tenantCurrency, policy),
            ResolvedTarget.Several several => Ambiguous(several.Candidates),
            _ => throw new InvalidOperationException($"Unknown target {target}."),
        };

    // Cases 1, 6 and 7.
    private static IdentificationDecision ForObligation(ObligationFacts obligation, Currency paymentCurrency)
    {
        var candidates = ImmutableArray.Create(new CandidateView(obligation.AccountId, obligation.Id, obligation.PaymentReference));
        if (obligation.Currency != paymentCurrency)
        {
            return Unidentified(
                PaymentExceptionReason.CurrencyMismatch,
                $"Payment in {paymentCurrency} for obligation {obligation.PaymentReference} in {obligation.Currency}.",
                candidates);
        }

        if (!obligation.IsPayableIn(paymentCurrency))
        {
            return new IdentificationDecision(
                obligation.AccountId, null, [], PaymentExceptionReason.ObligationNotPayable,
                $"Obligation {obligation.PaymentReference} is {obligation.Status} with nothing payable; the funds are held as unapplied.",
                candidates);
        }

        return new IdentificationDecision(obligation.AccountId, obligation.Id, [], null, $"Directed to {obligation.PaymentReference}.", []);
    }

    // Cases 2 and 3, plus a customer with nothing payable.
    private static IdentificationDecision ForCustomer(ResolvedTarget.OneCustomer customer, Currency paymentCurrency, Currency tenantCurrency, TenantPolicy policy)
    {
        var payable = customer.Obligations.Where(o => o.IsPayableIn(paymentCurrency)).OrderBy(o => o.Id.Value).ToImmutableArray();
        var candidates = payable.Select(o => new CandidateView(o.AccountId, o.Id, o.PaymentReference)).ToImmutableArray();

        if (payable.IsEmpty)
        {
            var hasAnyInCurrency = customer.Obligations.Any(o => o.Currency == paymentCurrency);
            if (paymentCurrency != tenantCurrency && !hasAnyInCurrency)
            {
                return Unidentified(PaymentExceptionReason.CurrencyMismatch, $"Payment in {paymentCurrency}; the customer has no obligations in that currency.", [new CandidateView(customer.AccountId, null, null)]);
            }

            return new IdentificationDecision(customer.AccountId, null, [], PaymentExceptionReason.NoPayableObligation,
                $"The customer has no payable obligation in {paymentCurrency}; the funds are held as unapplied.", []);
        }

        if (payable.Length == 1)
        {
            var only = payable[0];
            return policy.SoleObligation == SoleObligationPolicy.AutoAllocate
                ? new IdentificationDecision(customer.AccountId, only.Id, [], null, $"Directed to the customer's only payable obligation {only.PaymentReference}.", [])
                : new IdentificationDecision(customer.AccountId, null, [], PaymentExceptionReason.AllocationInstructionRequired,
                    "The payment did not say which obligation it is for; the funds are held as unapplied.", candidates);
        }

        return policy.MultipleObligations == MultipleObligationsPolicy.AllocateByStrategy
            ? new IdentificationDecision(customer.AccountId, null, [.. payable.Select(o => o.Id)], null,
                $"Eligible for allocation across {payable.Length} obligations by the tenant's strategy.", [])
            : new IdentificationDecision(customer.AccountId, null, [], PaymentExceptionReason.AllocationInstructionRequired,
                $"The customer has {payable.Length} payable obligations and the payment did not say which; the funds are held as unapplied.", candidates);
    }

    // Case 5: never allocated (I-20). Held by the customer only when every candidate is theirs.
    private static IdentificationDecision Ambiguous(ImmutableArray<CandidateView> candidates)
    {
        var accounts = candidates.Select(c => c.AccountId).Distinct().ToList();
        var detail = $"The reference matches {candidates.Length} candidates.";
        return accounts.Count == 1
            ? new IdentificationDecision(accounts[0], null, [], PaymentExceptionReason.AmbiguousReference, detail + " All belong to one customer; the funds are held as unapplied.", candidates)
            : Unidentified(PaymentExceptionReason.AmbiguousReference, detail, candidates);
    }

    private static IdentificationDecision Unidentified(PaymentExceptionReason reason, string detail, ImmutableArray<CandidateView> candidates) =>
        new(null, null, [], reason, detail, candidates);
}
