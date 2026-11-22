# Sample 07: inside an existing loan system

A lender already has a loan system with its own EF Core `DbContext`. When it disburses a loan, it writes its own
record and opens the repayment schedule in InstalmentFlow in **one** database transaction, using
`InstalmentFlowPostgres.Enlist`. Either both are saved or neither is. The sample shows a committed disbursement and
one rolled back after the schedule was opened.

```sh
docker run -d -e POSTGRES_PASSWORD=sample -p 5433:5432 postgres:17-alpine
export INSTALMENTFLOW_SAMPLE_PG='Host=localhost;Port=5433;Username=postgres;Password=sample'
dotnet run --project samples/07-ExistingLoanSystem
```

See [docs/integrations/existing-loan-system.md](../../docs/integrations/existing-loan-system.md).
