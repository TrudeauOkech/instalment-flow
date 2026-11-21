# M-Pesa (Daraja) adapter

`InstalmentFlow.Payments.Mpesa` connects the payments package to Safaricom's Daraja API. It uses:
- C2B confirmation and validation;
- M-Pesa Express (STK push) and its status query;
- Transaction Status;
- Pull Transactions;
- the organisation portal's statement export.

It never starts reversals or B2C payouts (ADR-010). Reversals reach the engine only from Safaricom's data: statements
and Pull Transactions.

## Verification status (M9 spike, 2026-11-21)

The roadmap's pre-work was to check every API against the official Daraja portal. That was not possible from here:
`developer.safaricom.co.ke` renders its API pages in the browser and needs an account, so its specifications could not
be read. The adapter is therefore built from what several independent sources agree on, and it accepts every variant
they disagree about. **Nothing below is confirmed against a Daraja sandbox yet.** Recording real sandbox payloads is
required before the 0.3.0 release (see "Before release").

Confidence:
- **Consistent**: several independent sources (client libraries, integration guides) agree, and doc 03 §7.2 had
  marked it verified.
- **One source**: a single source, or sources that disagree.
- **Unknown**: no usable source; the adapter assumes as little as possible.

| Item | What the adapter assumes | Confidence |
|---|---|---|
| OAuth | `GET /oauth/v1/generate?grant_type=client_credentials` with Basic auth; `access_token` and `expires_in` (seconds, about 3600, sent as a string) | Consistent |
| Expired token | A request with an expired token is refused with HTTP 401 or 404 and `errorCode` `404.001.03`; the adapter refreshes the token and retries once | One source |
| C2B registration | `POST /mpesa/c2b/v2/registerurl` (`ShortCode`, `ResponseType` `Completed` or `Cancelled`, `ConfirmationURL`, `ValidationURL`). v1 is legacy; the sandbox may still expect v1, so the path is configurable | Consistent (v2); one source (sandbox) |
| Callback URL rules | HTTPS, public host, no `localhost`; reportedly no `mpesa`, `m-pesa` or `safaricom` in the URL (sources disagree) | One source |
| C2B confirmation | `TransactionType`, `TransID`, `TransTime` (`yyyyMMddHHmmss`, Nairobi time), `TransAmount` (string), `BusinessShortCode`, `BillRefNumber`, `InvoiceNumber`, `OrgAccountBalance`, `ThirdPartyTransID`, `MSISDN`, `FirstName`, `MiddleName`, `LastName`. Numbers may also arrive as JSON numbers | Consistent |
| Payer number in C2B v2 | Masked (`2547 ***** 126`) per doc 03; one recent source says it is a SHA-256 hash in production. The adapter treats it as an opaque string and never matches on it | One source (they disagree) |
| C2B validation reply | `{"ResultCode": "0", "ResultDesc": "Accepted"}`; rejections `C2B00011` (MSISDN), `C2B00012` (account number), `C2B00013` (amount), and others | Consistent |
| Validation deadline | About 8 s, after which `ResponseType` applies | Consistent |
| STK push | `POST /mpesa/stkpush/v1/processrequest`. `Password` is base64(shortcode + passkey + timestamp); `Timestamp` is `yyyyMMddHHmmss` in Nairobi time; `TransactionType` is `CustomerPayBillOnline` or `CustomerBuyGoodsOnline`; `Amount` is a whole number. The reply has `MerchantRequestID`, `CheckoutRequestID`, `ResponseCode` `"0"` and `CustomerMessage` | Consistent |
| STK field limits | `AccountReference` at most 12 characters, `TransactionDesc` at most 13. The adapter refuses a longer reference rather than truncate it, so identification never breaks | One source |
| STK callback | `Body.stkCallback` with `MerchantRequestID`, `CheckoutRequestID`, `ResultCode` (number), `ResultDesc`, and on success `CallbackMetadata.Item[]` of `Name`/`Value` (`Amount`, `MpesaReceiptNumber`, `TransactionDate` (number, `yyyyMMddHHmmss`), `PhoneNumber`). It carries **no shortcode and no merchant reference** | Consistent |
| STK result codes | `0` paid; `1` insufficient funds; `1032` cancelled; `1037` no response; `2001` wrong PIN; `1019` expired; `1001` subscriber busy. Every non-zero code means no money moved | Consistent (1032 and 1037 described differently) |
| STK query | `POST /mpesa/stkpushquery/v1/query` (`BusinessShortCode`, `Password`, `Timestamp`, `CheckoutRequestID`) returns `ResultCode` and `ResultDesc` but **no receipt and no amount**; `errorCode` `500.001.1001` means it is still being processed | One source |
| Transaction Status | `POST /mpesa/transactionstatus/v1/query` with `Initiator`, `SecurityCredential`, `CommandID` `TransactionStatusQuery`, `TransactionID`, `PartyA`, `IdentifierType` (`4` for an organisation shortcode, per one source), `ResultURL`, `QueueTimeOutURL`, `Remarks`, `Occasion`. The result arrives later at `ResultURL`: `Result` with `ResultCode` and `ResultParameters.ResultParameter`, either an array or a single `{Key, Value}`. Keys include `ReceiptNo`, `Amount`, `TransactionStatus` (`Completed`, …), `FinalisedTime`, `DebitPartyName` and `CreditPartyName` | One source |
| Shared sandbox initiator | Sandbox initiators are reportedly locked (`8006`), so Transaction Status may not be testable there | One source |
| SecurityCredential | The initiator password, RSA-encrypted (PKCS #1 v1.5) with Safaricom's certificate (sandbox and production differ), as base64 | Consistent |
| Pull Transactions | `POST /pulltransactions/v1/register` and `/pulltransactions/v1/query` (`ShortCode`, `StartDate`, `EndDate` as `yyyy-MM-dd HH:mm:ss`, `OffSetValue`). Codes: `1000` found; `1001` none (sometimes sent as HTTP 500); the sandbox may spell keys with spaces (`Response Status`). Transactions are in `Response`, with `transactionId`, `trxDate`, `msisdn`, `transactiontype`, `billreference` and `amount` | One source; the sandbox has never returned a transaction to any source |
| Pull window | C2B only, the last 48 hours (doc 03) | Unknown (no source) |
| Reversal lines in Pull data | No source shows one. The adapter recognises a line as a reversal only if it names the reversed receipt (`originalTransactionId` or `linkedTransactionId`); otherwise it skips the line | Unknown |
| Org-portal statement CSV | Columns `Receipt No.`, `Completion Time`, `Initiation Time`, `Details`, `Transaction Status`, `Paid In`, `Withdrawn`, `Balance`, `Reason Type`, `Other Party Info`, `Linked Transaction ID`, `A/C No.`, after a preamble. The time format and preamble length are configurable | Unknown (from memory of exports; confirm with a real file) |
| Callback authenticity | No signature; protect with TLS, an unguessable path secret and Safaricom's source IP ranges (none published in any source read), then reconciliation | Consistent |
| Callback retries | Unspecified; possibly retried after a non-200. Treated as at-most-once, with duplicates possible | Unknown |

Sources read:
- [Code With Karani, Daraja in production](https://www.codewithkarani.com/blog/daraja-api-stk-push-c2b-b2c-production-guide)
- [Kenzobe, callback URLs](https://www.kenzobe.com/blog/mpesa-callback-urls)
- [newtonmunene99/mpesa-api #64](https://github.com/newtonmunene99/mpesa-api/pull/64) and [#65](https://github.com/newtonmunene99/mpesa-api/pull/65) (live sandbox captures of Pull Transactions)
- [SirWaithaka/payments-api Daraja client](https://pkg.go.dev/github.com/SirWaithaka/payments-api/clients/daraja)
- [Code With Karani, 1019 vs 1037 vs 1032](https://www.codewithkarani.com/blog/mpesa-1019-vs-1037-vs-1032-stk-push-failures)

## Consequences for the design

- **STK callbacks need their account from the endpoint.** The callback has no shortcode, so the host's endpoint passes
  it (`ProviderCallback.ProviderAccount`), normally from the callback URL. `MapInstalmentFlowCallbacks` routes `…/mpesa/stk/{shortCode}` this way.
- **A push whose reply was lost cannot be matched to its intent.** STK has no merchant reference, so if the request
  timed out before Safaricom answered, nothing ties the later callback to the intent. The payment is still recorded:
  without an account reference it becomes unidentified funds with an exception (ADR-009), and the intent expires.
  Hosts should prefer C2B (the customer types the reference) where that matters.
- **The STK query can say "paid", not "paid this receipt".** Without a receipt the adapter cannot record the payment,
  so it reports such an intent as pending. The callback or Pull Transactions brings the receipt.
- **Amounts are whole shillings.** A fractional amount in a callback is held in the inbox for a person (doc 07 case 14)
  and never rounded. A collection request for a fractional amount is refused before anything is sent.
- **The payer's number is never used for matching**, whether it is masked or hashed.

## Configuration

```csharp
var mpesa = new MpesaAdapter(httpClient, credentials, new MpesaOptions
{
    Environment = MpesaEnvironment.Sandbox,
    Accounts =
    {
        new MpesaAccount
        {
            ShortCode = "600123",
            StkCallbackUrl = new Uri("https://pay.example.com/hooks/k3y/mpesa/stk/600123"),
            ResultUrl = new Uri("https://pay.example.com/hooks/k3y/mpesa/transaction-status/600123"),
            QueueTimeoutUrl = new Uri("https://pay.example.com/hooks/k3y/mpesa/transaction-status-timeout/600123"),
        },
    },
});
var payments = InstalmentFlowPayments.CreateInMemory(flows, p => p.AddProvider(mpesa));
```

`IProviderCredentialSource` returns, per shortcode, the secrets named in `MpesaCredentialNames`:
- `ConsumerKey` and `ConsumerSecret`;
- `Passkey`, for STK;
- `InitiatorName`, `InitiatorPassword` and `Certificate` (PEM), for Transaction Status.

The adapter caches access tokens per consumer key and never logs a secret.

Callback kinds, passed as `ProviderCallback.Kind`:
- `c2b-confirmation`
- `stk` (with `ProviderAccount`)
- `transaction-status`
- `transaction-status-timeout` (acknowledged; nothing is recorded)

C2B validation is synchronous and never enters the inbox. `MapInstalmentFlowCallbacks` answers it at
`…/mpesa/validate`, within `Callbacks.ValidationBudget` (5 s), accepting unless the tenant opted into
`RejectUnknownReferences`. Without ASP.NET Core, use `MpesaValidation.Parse` and `MpesaValidation.Accept()` or `Reject(code)`.

Endpoint paths under `MapInstalmentFlowCallbacks("/hooks/{secret}")`:
- C2B confirmation: `/hooks/{secret}/mpesa/c2b-confirmation`
- C2B validation: `/hooks/{secret}/mpesa/validate`
- STK results: `/hooks/{secret}/mpesa/stk/{shortCode}`
- Transaction Status results: `/hooks/{secret}/mpesa/transaction-status/{shortCode}`
- Transaction Status time-outs: `/hooks/{secret}/mpesa/transaction-status-timeout/{shortCode}`

## Before release

1. Create a Daraja sandbox app with M-Pesa Express, C2B, Transaction Status and Pull Transactions enabled.
2. Run `samples/04-MpesaSandbox` with the sandbox credentials in environment variables.
3. Replace the fixtures in `tests/InstalmentFlow.Payments.Mpesa.Tests/Fixtures` with recorded ones, and mark each
   `sandbox` in that folder's README. Today they are `documented`. The sample saves Daraja's responses; capture the
   callbacks at the callback URL. Also capture a real (anonymised) org-portal statement export.
4. Update the table above, and doc 03 §7.2.
