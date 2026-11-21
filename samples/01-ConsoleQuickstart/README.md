# 01 — Console quickstart

An in-memory engine with one tenant (`acme-ke`, KES, Africa/Nairobi). A customer takes a KES 3,000 loan over three
months and pays KES 1,200 by paybill, quoting the loan's reference. The program prints the outcome (KES 1,000
allocated to the instalment due today, KES 200 held as unapplied funds), the outstanding balance, the statement and
the result of `VerifyAsync`.

```sh
dotnet run --project samples/01-ConsoleQuickstart
```
