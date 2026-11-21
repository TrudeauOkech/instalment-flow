# 02 — Pay-as-you-go phone financing

A KES 9,000 phone repaid at KES 50 a day over 180 days. The tenant uses
`FutureInstalmentPolicy.ApplyToNextInstalments`, so any payment beyond what is due "buys days" ahead. The program
simulates a week of irregular payments, sweeps held credit each morning, and derives the device's lock state from
the paid-through date: unlocked while it is today or later, locked when the customer falls behind (the missed
weekend), unlocked again when they catch up.

```sh
dotnet run --project samples/02-PaygPhoneFinancing
```
