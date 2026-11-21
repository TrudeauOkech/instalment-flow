# Samples

Each sample is a small console program that checks its own result and ends with `VerifyAsync`, so it exits
non-zero if anything is wrong. CI builds and runs all of them.

| Sample | Shows |
|---|---|
| [01-ConsoleQuickstart](01-ConsoleQuickstart/) | The smallest program: a tenant, a customer, a loan, a paybill payment, a statement and verification. |
| [02-PaygPhoneFinancing](02-PaygPhoneFinancing/) | Pay-as-you-go: KES 50 a day, irregular micro-payments buying days ahead, and the paid-through date driving device lock. |
| [03-MultipleLoans](03-MultipleLoans/) | Two loans per customer: referenced payments never spill over, unreferenced ones are held for an operator, and a tenant's opt-in to proportional allocation. |

Run one with `dotnet run --project samples/01-ConsoleQuickstart`. Samples 04 (ASP.NET Core with M-Pesa and
PostgreSQL) and 05 (inside an existing loan system's `DbContext`) arrive with the stores and adapters they need.
