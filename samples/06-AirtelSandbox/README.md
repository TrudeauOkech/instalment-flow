# Sample 06: Airtel Money sandbox

Runs the Airtel Money adapter against Airtel's UAT environment: a USSD push through a payment intent, followed by
enquiries. Without credentials it prints what to set and exits successfully.

```sh
export AIRTEL_CLIENT_ID=... AIRTEL_CLIENT_SECRET=... AIRTEL_PHONE=733123456
export AIRTEL_RECORD_DIR=./recorded
dotnet run --project samples/06-AirtelSandbox
```

To settle the disputed collection path, run it once with `AIRTEL_COLLECTION_PATH=/merchant/v1/payments/` as well.
Callbacks go to the URL configured in Airtel's portal; point it at a host running `MapInstalmentFlowCallbacks`
(sample 05) to capture them. See [docs/integrations/airtel.md](../../docs/integrations/airtel.md).
