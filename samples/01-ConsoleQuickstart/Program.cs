// Sample 01: the smallest useful InstalmentFlow program. An in-memory engine, one customer, one loan, a paybill
// payment, and the questions a lender asks next: what is owed, what was paid, does the ledger balance?
using InstalmentFlow;

var factory = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"));
var flow = factory.ForTenant("acme-ke");
var today = DateOnly.FromDateTime(DateTime.UtcNow);   // the sample's only use of the wall clock

var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-C-1001", ExternalId = "C-1001" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan-L-2041",
    Account = customer.AccountId,
    ExternalId = "L-2041",
    PaymentReference = "LN-100001",
    Components = [Component.Principal(Money.Kes(3_000m))],
    Plan = PlanSpec.Equal(Frequency.Monthly, 3, today),   // 3 × KES 1,000, the first due today
});

// An M-Pesa paybill confirmation: the customer quoted the loan's reference. Recording is idempotent.
var payment = await flow.Payments.RecordAsync(new RecordPaymentRequest
{
    IdempotencyKey = "mpesa:600123:TJ3K9XYZ",
    Source = new ExternalReference("mpesa:600123", "TJ3K9XYZ"),
    Amount = Money.Kes(1_200m),
    ValueAt = DateTimeOffset.UtcNow,
    Target = PaymentTarget.ForReference("LN-100001"),
});
Console.WriteLine($"{payment.Outcome}: allocated {payment.Allocated}, unapplied {payment.UnappliedAfter}");

var loan = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN-100001"));
Console.WriteLine($"Outstanding {loan.Outstanding}; paid through {loan.PaidThroughDate:yyyy-MM-dd}");
foreach (var line in await flow.Queries.GetStatementAsync(customer.AccountId, today, today).ToListAsync())
{
    Console.WriteLine($"  {line.EffectiveDate:yyyy-MM-dd} {line.Description,-36} {line.Amount,14} balance {line.Balance,14}");
}

var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant);
Console.WriteLine(report.Passed ? "Ledger verified." : string.Join(Environment.NewLine, report.Violations));
return report.Passed && loan.Outstanding == Money.Kes(2_000m) ? 0 : 1;
