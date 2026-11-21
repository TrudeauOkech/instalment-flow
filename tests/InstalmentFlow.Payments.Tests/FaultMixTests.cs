using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>
/// Whatever the provider does to its callbacks (duplicates, drops, delays, reordering) the engine ends up with exactly
/// the statement's payments once callbacks, processing and one reconciliation have run.
/// </summary>
public sealed class FaultMixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, Harness.IsPostgres ? 3 : 25)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Every_payment_is_recorded_exactly_once_whatever_the_faults(int seed)
    {
        var random = new Random(seed);
        var h = await Harness.CreateAsync();
        var faults = new[] { SimulatedFaults.None, SimulatedFaults.Duplicate, SimulatedFaults.Drop, SimulatedFaults.Reorder, SimulatedFaults.Duplicate | SimulatedFaults.Reorder };
        var expected = new Dictionary<string, decimal>();
        var count = random.Next(5, 20);
        for (var i = 0; i < count; i++)
        {
            var amount = random.Next(1, 200);
            var paid = h.PayBill(amount, reference: random.Next(4) == 0 ? null : Harness.Loan, faults: faults[random.Next(faults.Length)],
                delay: TimeSpan.FromMinutes(random.Next(0, 240)));
            expected[paid.TransactionId] = amount;
            h.Clock.Advance(TimeSpan.FromMinutes(random.Next(0, 30)));
            if (random.Next(3) == 0)
            {
                await h.DeliverAndProcessAsync();
            }
        }

        await h.ReconcileAsync(from: h.Clock.Now.AddDays(-2));
        h.Clock.Advance(TimeSpan.FromHours(5));
        await h.Simulator.DeliverAllAsync(Ct);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        var recorded = await h.PaymentsAsync();
        Assert.Equal(expected.OrderBy(e => e.Key), recorded.Select(p => KeyValuePair.Create(p.Source.Value, p.Amount.Amount)).OrderBy(e => e.Key));
        Assert.Empty(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        var after = await h.ReconcileAsync(from: h.Clock.Now.AddDays(-2));
        Assert.All(after.Lines, l => Assert.Equal(ReconciliationClassification.Matched, l.Classification));
        Assert.Empty(after.MissingAtProvider);
        await h.AssertLedgerVerifiesAsync();
    }
}
