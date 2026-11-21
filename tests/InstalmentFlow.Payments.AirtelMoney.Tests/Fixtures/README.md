# Airtel Money contract fixtures

Each file is an Airtel Open API payload the adapter must handle; `{ref}` is replaced with the merchant reference (the
intent ID) in tests. **Provenance:** see docs/integrations/airtel.md. No fixture has been recorded from Airtel's
sandbox yet. Before the 0.4.0 release, each is replaced by a recorded one and marked `sandbox` here.

| Fixture | Provenance |
|---|---|
| `oauth.json` | documented (`expires_in` as a string; sources also show a number) |
| `push-accepted.json`, `push-not-successful.json` | documented; the meaning of `DP…` codes is disputed between sources |
| `enquiry-ts.json`, `enquiry-tf.json`, `enquiry-ta.json`, `enquiry-tip.json` | documented (`data.transaction` wrapper) |
| `enquiry-ts-flat.json` | documented by one source (no `data` wrapper) |
| `callback-ts.json`, `callback-tf.json` | documented (no amount) |
| `callback-with-hash.json` | one source mentions a `hash` when "authenticated callbacks" are enabled; its algorithm is unknown, so it is ignored |
