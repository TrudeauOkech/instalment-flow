# Future: provider refund and reversal initiation (post-1.0)

Status: **design placeholder**. Nothing here ships in 1.0 (ADR-010).

In 1.0, InstalmentFlow *records* refunds (`Requested → Confirmed | Failed`) and reversals. The host executes payouts. A later release may add optional provider initiation behind capability flags.

Requirements already identified:
- `IRefundInitiator` / `IReversalInitiator` in `InstalmentFlow.Payments`, implemented only by adapters whose provider supports it:
  - M-Pesa: Reversal API (C2B only, asynchronous), or B2C;
  - Airtel Money: refund by `airtel_money_id`.
- Initiation must be idempotent and go through a `PayoutIntent` state machine. Its states mirror `PaymentIntent`, including **Ambiguous**: a timeout is not a failure.
- The engine's refund record moves to `Confirmed` only on a provider result or reconciliation, never on request acceptance.
- Approval workflow (maker-checker) and limits stay with the host. The initiator only executes approved instructions.
- Initiator credentials (M-Pesa Initiator/SecurityCredential, Airtel PIN encryption) come per tenant from `IProviderCredentialSource`.

Open items to verify before design: whether M-Pesa partial reversals are supported, and Airtel's partial-refund semantics.
