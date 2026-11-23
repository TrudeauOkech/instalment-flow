# Sample 04: M-Pesa sandbox

Runs the M-Pesa adapter against the Daraja sandbox:
- an STK push to the sandbox test phone, through a payment intent, followed by status queries;
- C2B URL registration and a simulated paybill payment.

Without credentials it prints what to set and exits successfully.

```sh
export MPESA_CONSUMER_KEY=... MPESA_CONSUMER_SECRET=... MPESA_PASSKEY=...
export MPESA_CALLBACK_BASE=https://your-tunnel.example.com/hooks/your-secret
export MPESA_RECORD_DIR=./recorded
dotnet run --project samples/04-MpesaSandbox
```

The sample saves Daraja's responses in `MPESA_RECORD_DIR`, with access tokens redacted. Callbacks go to
`MPESA_CALLBACK_BASE`, so capture them there. Together they are the sandbox fixtures needed before the adapter is published (see
[docs/integrations/mpesa.md](../../docs/integrations/mpesa.md)).
