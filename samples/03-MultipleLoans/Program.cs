// Sample 03: a customer with two loans (ADR-009). A payment that quotes a loan's reference pays that loan only and
// never spills onto the other (I-21). A payment that quotes nothing is held and raised as an exception, because
// guessing which loan it was for is never safe; an operator resolves it. A tenant that has the contractual right
// can opt in to spreading such payments across loans with a strategy (here Proportional).
using InstalmentFlow;

var clock = new SampleClock(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero));
var today = new DateOnly(2026, 12, 1);

// ----- Default policy: hold and ask -----
var flow = Tenant("bank-ke", p => p);
var customer = await OpenCustomerAsync(flow);

await PayAsync(flow, "TX1", 9_000m, PaymentTarget.ForReference("BIKE-1"));   // 8,000 due on the bike; 1,000 held for it
var held = await PayAsync(flow, "TX2", 2_500m, PaymentTarget.ForCustomer(customer));
Console.WriteLine($"Unreferenced payment: {held.Outcome}, exception {held.Exception!.Reason}");

await flow.Exceptions.AllocateToObligationAsync(new AllocateToObligationRequest
{
    IdempotencyKey = "resolve-TX2",
    ExceptionId = held.Exception.ExceptionId,
    Obligation = ObligationRef.ByReference("SCHOOL-1"),
    ResolvedBy = "ops@bank",
    Note = "Customer called: school fees",
});

var bike = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("BIKE-1"));
var school = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("SCHOOL-1"));
var account = await flow.Queries.GetAccountAsync(customer);
Console.WriteLine($"Bike outstanding {bike.Outstanding}, school outstanding {school.Outstanding}, held for the bike {account.Unapplied.Single()}");
var ok = bike.Arrears == Money.Kes(0m) && school.Arrears == Money.Kes(0m) && account.Unapplied.Single() == Money.Kes(1_000m);

// ----- Opt-in: spread unreferenced payments in proportion to what is due -----
var spread = Tenant("sacco-ke", p => p with
{
    MultipleObligations = MultipleObligationsPolicy.AllocateByStrategy,
    Allocation = AllocationStrategies.Proportional.Ref,
});
var member = await OpenCustomerAsync(spread);
var proportional = await PayAsync(spread, "TX3", 5_250m, PaymentTarget.ForCustomer(member));
foreach (var line in proportional.Lines.GroupBy(l => l.ObligationId))
{
    var reference = (await spread.Queries.GetObligationAsync(line.Key)).PaymentReference;
    Console.WriteLine($"  {reference}: {Money.Sum(Currency.KES, line.Select(l => l.Amount))}");
}

ok &= proportional.Lines.Sum(l => l.Amount.Amount) == 5_250m && proportional.Exception is null;

foreach (var tenant in new[] { flow, spread })
{
    var report = await tenant.Queries.VerifyAsync(VerifyScope.Tenant);
    ok &= report.Passed;
    Console.WriteLine($"{tenant.Tenant}: {(report.Passed ? "ledger verified" : string.Join("; ", report.Violations))}");
}

return ok ? 0 : 1;

IInstalmentFlow Tenant(string id, Func<TenantPolicy, TenantPolicy> policy) =>
    InstalmentFlowFactory.CreateInMemory(b => b.UseTimeProvider(clock).AddTenant(id, Currency.KES, "Africa/Nairobi", policy)).ForTenant(id);

async Task<AccountId> OpenCustomerAsync(IInstalmentFlow tenant)
{
    var id = (await tenant.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-C-9", ExternalId = "C-9" })).AccountId;
    await OpenLoanAsync(tenant, id, "BIKE-1", 8_000m, 3);      // 3 × 8,000
    await OpenLoanAsync(tenant, id, "SCHOOL-1", 2_500m, 4);    // 4 × 2,500
    return id;
}

async Task OpenLoanAsync(IInstalmentFlow tenant, AccountId id, string reference, decimal instalment, int count) =>
    await tenant.Obligations.OpenAsync(new OpenObligationRequest
    {
        IdempotencyKey = $"loan-{reference}",
        Account = id,
        ExternalId = reference,
        PaymentReference = reference,
        Components = [Component.Principal(Money.Kes(instalment * count))],
        Plan = PlanSpec.Equal(Frequency.Monthly, count, today),
    });

async Task<RecordPaymentResult> PayAsync(IInstalmentFlow tenant, string transId, decimal amount, PaymentTarget target) =>
    await tenant.Payments.RecordAsync(new RecordPaymentRequest
    {
        IdempotencyKey = $"mpesa:600123:{transId}",
        Source = new ExternalReference("mpesa:600123", transId),
        Amount = Money.Kes(amount),
        ValueAt = clock.GetUtcNow(),
        Target = target,
    });

/// <summary>A fixed clock for a reproducible run.</summary>
internal sealed class SampleClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
