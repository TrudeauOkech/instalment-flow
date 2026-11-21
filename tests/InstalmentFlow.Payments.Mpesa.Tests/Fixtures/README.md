# M-Pesa contract fixtures

Each file is a Daraja payload the adapter must handle. **Provenance** says where its shape comes from (see
docs/integrations/mpesa.md). Before the 0.3.0 release, every `documented` fixture is replaced by one recorded from the
Daraja sandbox and marked `sandbox`.

| Fixture | Provenance |
|---|---|
| `oauth.json`, `token-expired.json` | documented |
| `c2b-confirmation.json`, `c2b-validation.json` | documented |
| `c2b-confirmation-hashed.json` | documented: hashed MSISDN and numbers as JSON numbers, the variants sources disagree on |
| `stk-push-response.json`, `stk-push-error.json` | documented |
| `stk-callback-success.json`, `stk-callback-cancelled.json` | documented |
| `stk-query-success.json`, `stk-query-cancelled.json`, `stk-query-processing.json` | documented |
| `transaction-status-result.json` | documented |
| `transaction-status-locked.json` | third-party sandbox capture (ResultCode 8006, a single `ResultParameter` object) |
| `pull-register-sandbox.json`, `pull-query-empty-sandbox.json` | third-party sandbox captures (keys with spaces; `1001`) |
| `pull-query-page.json` | documented: no source has seen a sandbox page with transactions; includes date and amount variants |
| `org-portal-statement.csv` | assumed: column names from memory of portal exports; replace with a real (anonymised) export |
