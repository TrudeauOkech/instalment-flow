using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>
/// Large payments reported by callbacks are held until a statement line confirms them (R-I3,
/// <see cref="PaymentsBuilder.ConfirmByStatementAbove"/>): a forged callback cannot credit a large amount.
/// </summary>
public sealed class StatementConfirmationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<Harness> CreateAsync() => Harness.CreateAsync(b => b.ConfirmByStatementAbove("sim", Money.Kes(1_000m)));

    [Fact]
    public async Task A_large_callback_payment_waits_for_the_statement_and_a_small_one_does_not()
    {
        var h = await CreateAsync();
        var large = h.PayBill(900m + 600m);
        h.PayBill(300m);

        await h.DeliverAndProcessAsync();

        Assert.Equal(Money.Kes(300m), await h.LoanPaidAsync());
        var held = Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        Assert.StartsWith("Held: awaiting statement confirmation", held.Error, StringComparison.Ordinal);

        var report = await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Contains(report.Lines, l => l.TransactionId == large.TransactionId && l.Classification == ReconciliationClassification.MissingInEngine);
        Assert.Equal(Money.Kes(500m), await h.LoanPaidAsync());   // the due instalment; the rest of the 1,500 is the customer's credit
        Assert.Equal(Money.Kes(1_500m), (await h.PaymentsAsync()).Single(p => p.Source.Value == large.TransactionId).Amount);
        Assert.Empty(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_forged_large_callback_never_credits_anything()
    {
        var h = await CreateAsync();
        var body = """{"type":"payment","account":"600123","transactionId":"FORGED1","amount":"50000","currency":"KES","time":"2026-03-02T05:00:00Z","reference":"LN-1"}"""u8.ToArray();
        await h.Payments.Inbox.ReceiveAsync(new ProviderCallback("sim", "payment", body, new Dictionary<string, string> { [SimulatedProvider.SignatureHeader] = h.Simulator.Sign(body) }), Ct);

        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Empty(await h.PaymentsAsync());
        Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_statement_that_contradicts_a_held_amount_keeps_it_held_and_raises_a_discrepancy()
    {
        var h = await CreateAsync();
        h.Simulator.Pay(new SimulatedPayment { ProviderAccount = Harness.Paybill, Amount = Money.Kes(5_000m), AccountReference = Harness.Loan, Faults = SimulatedFaults.AmountMismatch });
        await h.DeliverAndProcessAsync();

        var report = await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(ReconciliationClassification.AmountMismatch, Assert.Single(report.Lines).Classification);
        var discrepancy = Assert.Single(report.NewDiscrepancies);
        Assert.Equal((Money.Kes(5_001m), Money.Kes(5_000m)), (discrepancy.StatementAmount, discrepancy.EngineAmount));
        Assert.Empty(await h.PaymentsAsync());
    }
}
