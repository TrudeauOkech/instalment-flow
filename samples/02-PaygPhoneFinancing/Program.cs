// Sample 02: pay-as-you-go phone financing. A KES 9,000 phone is repaid at KES 50 a day. Customers pay whenever
// they can, in any amount; each payment "buys days" (FutureInstalmentPolicy.ApplyToNextInstalments), and the
// device-lock service unlocks the phone while the paid-through date is today or later.
using InstalmentFlow;

var clock = new SampleClock(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero));   // 09:00 in Nairobi
var factory = InstalmentFlowFactory.CreateInMemory(b => b
    .UseTimeProvider(clock)
    .AddTenant("payg-ke", Currency.KES, "Africa/Nairobi", p => p with { FutureInstalments = FutureInstalmentPolicy.ApplyToNextInstalments }));
var flow = factory.ForTenant("payg-ke");
var start = new DateOnly(2026, 12, 1);

var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-C-77", ExternalId = "C-77" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan-PH-77",
    Account = customer.AccountId,
    ExternalId = "PH-77",
    PaymentReference = "PH77",
    Components = [Component.Principal(Money.Kes(9_000m))],
    Plan = PlanSpec.Equal(Frequency.Daily, 180, start),
    Description = "Smartphone, 180 days at KES 50",
});

// (day offset, amount): a week of irregular micro-payments, including a missed weekend.
(int Day, decimal Amount)[] payments = [(0, 50m), (1, 120m), (2, 30m), (5, 20m), (6, 300m)];
var expectedLocked = new[] { false, false, false, false, true, true, false };
var n = 0;
for (var day = 0; day < 7; day++)
{
    clock.Now = new DateTimeOffset(2026, 12, 1 + day, 6, 0, 0, TimeSpan.Zero);
    foreach (var (_, amount) in payments.Where(p => p.Day == day))
    {
        n++;
        await flow.Payments.RecordAsync(new RecordPaymentRequest
        {
            IdempotencyKey = $"mpesa:600123:PAYG{n}",
            Source = new ExternalReference("mpesa:600123", $"PAYG{n}"),
            Amount = Money.Kes(amount),
            ValueAt = clock.Now,
            Target = PaymentTarget.ForReference("PH77"),
        });
    }

    // Credit held from earlier payments buys today's instalment too.
    await flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = $"daily-{day}", Account = customer.AccountId });

    var phone = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("PH77"));
    var today = start.AddDays(day);
    var locked = phone.PaidThroughDate is not { } through || through < today;
    Console.WriteLine($"{today:ddd dd MMM}: paid through {phone.PaidThroughDate:dd MMM}, arrears {phone.Arrears}, device {(locked ? "LOCKED" : "unlocked")}");
    if (locked != expectedLocked[day])
    {
        Console.Error.WriteLine("Unexpected lock state.");
        return 1;
    }
}

var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant);
Console.WriteLine(report.Passed ? "Ledger verified." : string.Join(Environment.NewLine, report.Violations));
return report.Passed ? 0 : 1;

/// <summary>A clock the sample moves day by day.</summary>
internal sealed class SampleClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
