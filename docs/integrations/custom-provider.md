# Writing a provider adapter

Connect any payment provider (a bank's virtual accounts, another mobile-money network, a card processor) by
implementing the payments package's interfaces. The M-Pesa and Airtel adapters are complete examples; the
[simulator](simulator.md) is the smallest.

## The contract

```csharp
public sealed class MyBankAdapter : IPaymentProviderAdapter   // + ICollectionInitiator, ITransactionStatusQuery, IStatementSource if supported
{
    public string Provider => "mybank";                         // lower case; payment sources become "mybank:{account}"
    public ProviderCapabilities Capabilities => ProviderCapabilities.CustomerInitiatedCallbacks | ProviderCapabilities.AuthenticatedCallbacks;

    public ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken ct)
    {
        if (!SignatureIsValid(callback)) return ValueTask.FromResult(CallbackParseResult.Reject("bad signature"));
        // …parse…
        return ValueTask.FromResult(CallbackParseResult.Accept(
            NormalisedPaymentEvent.PaymentReceived(Provider, account, receipt, Money.Of(amount, "KES"), valueAt, accountReference, payer)));
    }
}
```

Rules every adapter follows:

1. **Declare exactly what you implement.** Each capability flag must match the interfaces the adapter implements, or
   start-up fails.
2. **Never throw for bad input.** Reject malformed or forged callbacks with a reason; they are not stored. Limit body
   size and JSON depth.
3. **Never correct the provider.** If a payload breaks a provider rule but is authentic (an impossible amount), use
   `CallbackParseResult.Hold`. The event is stored for a person and never rounded or guessed.
4. **Let the event derive its deduplication key.** Use the `NormalisedPaymentEvent` factories. The same receipt from a
   callback and from a statement then becomes one inbox entry.
5. **Say when a notification cannot be trusted.** If callbacks are unsigned and the provider has a status query,
   pass `requiresVerification: true` for collection results. The inbox then confirms with your `QueryAsync` first.
6. **A timeout is not a failure.** In `InitiateAsync`, return `Unknown` (or throw) when you cannot tell whether the
   provider started the collection, and `Rejected` only when it certainly did not.
7. **Take secrets per account** from `IProviderCredentialSource`. Never log them or keep them in options.
8. **Initiate nothing that moves money out.** Refunds and reversals are recorded, not initiated, in 1.0 (ADR-010).

## Tests

Copy the M-Pesa or Airtel test layout:
- record fixtures of real payloads, with their provenance;
- run contract tests against an in-process fake API;
- fuzz the parser with mutated and random payloads;
- add end-to-end tests through the inbox, intents and reconciliation.
