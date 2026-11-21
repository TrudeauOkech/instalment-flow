# Airtel Money adapter

`InstalmentFlow.Payments.AirtelMoney` connects the payments package to the Airtel Africa Open API. It does three
things:
- USSD push collection;
- transaction enquiry;
- collection callbacks.

It never starts refunds or disbursements (ADR-010).

## Verification status (M11 spike, 2026-11-22)

Airtel's developer portal could not be read from here, so, as for M-Pesa, the adapter follows what independent
sources agree on and is defensive wherever they disagree. The sources are listed below. They disagree more than the
M-Pesa sources did. **Nothing below is confirmed against Airtel's sandbox (UAT).** That check is required before the
0.4.0 release.

| Item | What the adapter assumes | Confidence |
|---|---|---|
| Hosts | `openapiuat.airtel.africa` (sandbox) and `openapi.airtel.africa` (production). One source gives `openapi(uat).airtelkenya.com` for Kenya, so `BaseAddress` can be overridden | One source each (they disagree) |
| OAuth | `POST /auth/oauth2/token` with JSON `client_id`, `client_secret` and `grant_type: client_credentials`, returning `access_token`, `expires_in` and `token_type` | Consistent |
| Token lifetime | Sources say 180 s or 3600 s, as a number or a string. The adapter refreshes at 80% of the stated lifetime (180 s if none is stated), and once more after a 401 | Disputed |
| Collection path | `/merchant/v2/payments/` by default, with `/merchant/v1/payments/` as an option (`CollectionPath`) | Disputed |
| Collection request | Headers `X-Country` and `X-Currency`. Body: `reference`, `subscriber {country, currency, msisdn}` and `transaction {amount, country, currency, id}`, where `id` is our merchant reference (the intent ID) | Consistent |
| Subscriber number | Without the country code or a leading zero (`733123456`) by default. One source, for Congo, uses a leading zero. `MsisdnFormat` chooses | Mostly consistent |
| Collection reply | `status.success: true` means accepted. Anything else is **ambiguous**, never failed, because the two sources that list `DP0080000100x` codes disagree on every one. A client error (4xx) is a refusal | Disputed codes |
| Callback | `transaction {id, message, status_code, airtel_money_id}`, with **no amount**. One source mentions a `hash` field when "authenticated callbacks" are enabled, but gives no algorithm; others say callbacks are unsigned | Disputed |
| Enquiry | `GET /standard/v1/payments/{id}` with the same headers. `transaction {airtel_money_id, id, message, status}`, wrapped in `data` or not (the adapter reads both). No amount | Mostly consistent |
| Status codes | `TS` succeeded; `TF` failed; `TE` expired (failed); `TA` ambiguous; `TIP` (or `TI`) in progress. Anything else is pending | Mostly consistent (`TA` is "pending" in some sources) |
| Customer-initiated payments (till) | No source documents notifications for payments the customer starts, so the adapter does not offer them | Unknown |
| Statements | No source documents an API or export format. Reconcile exported files with the generic `CsvStatementSource` once a format is known | Unknown |
| Amount precision | Unknown. Whole units only by default (`AllowFractionalAmounts`) | Unknown |

Sources read:
- [Mctaba, Airtel Money in Kenya](https://mctaba.com/learn/mpesa/airtel-money-integration-kenya)
- [Salami Airtel SDK docs](https://salami.co.ke/docs/api/sdk-airtel)
- [go-payup-airtel-money](https://pkg.go.dev/github.com/sharkfoursix/go-payup-airtel-money)
- [osenco/airtel](https://github.com/osenco/airtel)
- [lepresk/momo-api](https://github.com/lepresk/momo-api)
- [cyrus-48/paysasa #10](https://github.com/cyrus-48/paysasa/pull/10)

## Consequences for the design

- **Callbacks are prompts, not proof.** A callback is unsigned, or signed in a way nobody documents, and carries no
  amount. So the adapter marks its events `RequiresVerification`, and the inbox confirms each with an enquiry first:
  - a success is recorded only if the enquiry says `TS`, so a forged success records nothing;
  - a failure fails the intent only if the enquiry agrees, so a forged failure cannot cancel a real payment;
  - while the enquiry says in progress, the entry is retried with back-off.
- **The amount recorded is the amount requested.** Neither the callback nor the enquiry states the amount, and a
  USSD push collects exactly what was asked.
- **Our reference identifies the collection.** The intent ID goes out as `transaction.id`, so callbacks and enquiries
  match the intent even if the push's reply was lost.
- **`TA` makes an intent ambiguous.** The resolver keeps asking until `TS`, `TF` or the intent's time to live.

## Configuration

```csharp
var airtel = new AirtelAdapter(httpClient, credentials, new AirtelOptions
{
    Environment = AirtelEnvironment.Sandbox,
    Accounts = { new AirtelAccount { Account = "acme-ke-app", Country = "KE", Currency = Currency.KES } },
});
var payments = InstalmentFlowPayments.CreateInMemory(flows, p => p.AddProvider(airtel));
await payments.Routing.MapAsync("airtel", "acme-ke-app", "acme-ke");
```

`IProviderCredentialSource` returns, per account, `AirtelCredentialNames.ClientId` and `ClientSecret`. Set the
callback URL in Airtel's portal to `https://your-host/hooks/{secret}/airtel/collection/{account}`. The account comes
from the path, because Airtel's callbacks do not name it.

## Before release

1. Create an Airtel Open API application (UAT) for Kenya with collections enabled.
2. Run `samples/06-AirtelSandbox` with its credentials. It saves every response, with tokens redacted.
3. Settle each disputed row above:
   - the collection path and phone-number format;
   - whether amounts appear anywhere;
   - the `TA` meaning;
   - what `hash` is, and whether it can authenticate callbacks.
4. Replace the fixtures in `tests/InstalmentFlow.Payments.AirtelMoney.Tests/Fixtures` with recorded ones, and update
   this page and doc 03 §7.2.
