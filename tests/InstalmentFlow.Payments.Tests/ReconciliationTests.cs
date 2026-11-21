using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>Reconciliation (doc 03 §7.3): missing payments are ingested once; anything else becomes a discrepancy, never an automatic change.</summary>
public sealed class ReconciliationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_callback_plus_a_statement_line_ingests_the_payment_once()
    {
        var h = await Harness.CreateAsync();
        var lost = h.PayBill(300m, faults: SimulatedFaults.Drop);
        await h.DeliverAndProcessAsync();
        Assert.Empty(await h.PaymentsAsync());

        var first = await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        var second = await h.ReconcileAsync();

        Assert.Equal(ReconciliationClassification.MissingInEngine, Assert.Single(first.Lines).Classification);
        Assert.Equal(ReconciliationClassification.Matched, Assert.Single(second.Lines).Classification);
        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Equal(lost.TransactionId, payment.Source.Value);
        Assert.Equal(Money.Kes(300m), await h.LoanPaidAsync());   // identified from the statement's account reference
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_callback_that_arrives_after_reconciliation_ingested_its_payment_is_a_duplicate()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(300m, delay: TimeSpan.FromHours(3));

        await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        h.Clock.Advance(TimeSpan.FromHours(3));
        await h.DeliverAndProcessAsync();

        Assert.Single(await h.PaymentsAsync());
        Assert.Equal(Money.Kes(300m), await h.LoanPaidAsync());
    }

    [Fact]
    public async Task A_payment_missing_at_the_provider_raises_a_discrepancy_and_changes_nothing()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(200m);
        await h.DeliverAndProcessAsync();
        await h.Flow.Payments.RecordAsync(new RecordPaymentRequest   // e.g. a forged callback that got through
        {
            IdempotencyKey = "forged", Source = new ExternalReference(Harness.Source, "NOTREAL1"), Amount = Money.Kes(5_000m), ValueAt = h.Clock.Now.AddMinutes(-5),
            Target = PaymentTarget.ForReference(Harness.Loan),
        }, Ct);

        var report = await h.ReconcileAsync();
        var again = await h.ReconcileAsync();

        var missing = Assert.Single(report.MissingAtProvider);
        Assert.Equal("NOTREAL1", missing.TransactionId);
        var discrepancy = Assert.Single(report.NewDiscrepancies);
        Assert.Equal((DiscrepancyKind.MissingAtProvider, Money.Kes(5_000m)), (discrepancy.Kind, discrepancy.EngineAmount));
        Assert.Empty(again.NewDiscrepancies);   // raised once, however often it is seen
        Assert.Single(await h.TenantPayments.Discrepancies.ListOpenAsync(Ct));
        var forged = (await h.PaymentsAsync()).Single(p => p.Source.Value == "NOTREAL1");
        Assert.Equal(Money.Kes(0m), forged.Reversed);   // no automatic change
    }

    [Fact]
    public async Task An_amount_mismatch_raises_a_discrepancy()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(300m, faults: SimulatedFaults.AmountMismatch);
        await h.DeliverAndProcessAsync();

        var report = await h.ReconcileAsync();

        Assert.Equal(ReconciliationClassification.AmountMismatch, Assert.Single(report.Lines).Classification);
        var discrepancy = Assert.Single(report.NewDiscrepancies);
        Assert.Equal((Money.Kes(301m), Money.Kes(300m)), (discrepancy.StatementAmount, discrepancy.EngineAmount));
    }

    [Fact]
    public async Task A_statement_only_reversal_raises_a_discrepancy_by_default()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m);
        await h.DeliverAndProcessAsync();
        h.Simulator.Reverse(paid.TransactionId, faults: SimulatedFaults.Drop);

        var report = await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Contains(report.Lines, l => l.Classification == ReconciliationClassification.ReversedAtProvider);
        Assert.Equal(DiscrepancyKind.ReversedAtProvider, Assert.Single(report.NewDiscrepancies).Kind);
        Assert.Equal(Money.Kes(0m), Assert.Single(await h.PaymentsAsync()).Reversed);
    }

    [Fact]
    public async Task A_statement_only_reversal_is_reversed_when_the_tenant_opts_in()
    {
        var h = await Harness.CreateAsync(b => b.UseReversedAtProviderPolicy(Harness.Tenant, ReversedAtProviderPolicy.AutoReverse));
        var paid = h.PayBill(300m);
        await h.DeliverAndProcessAsync();
        h.Simulator.Reverse(paid.TransactionId, faults: SimulatedFaults.Drop);

        var report = await h.ReconcileAsync();
        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        var again = await h.ReconcileAsync();

        Assert.Empty(report.NewDiscrepancies);
        Assert.Equal(Money.Kes(300m), Assert.Single(await h.PaymentsAsync()).Reversed);
        Assert.DoesNotContain(again.Lines, l => l.Classification == ReconciliationClassification.ReversedAtProvider);
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_reversal_the_callback_already_reported_is_matched()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m);
        await h.DeliverAndProcessAsync();
        h.Simulator.Reverse(paid.TransactionId, Money.Kes(100m));
        await h.DeliverAndProcessAsync();

        var report = await h.ReconcileAsync();

        Assert.All(report.Lines, l => Assert.Equal(ReconciliationClassification.Matched, l.Classification));
        Assert.Empty(report.NewDiscrepancies);
    }

    [Fact]
    public async Task A_run_continues_from_the_previous_one_with_an_overlap()
    {
        var h = await Harness.CreateAsync(b => b.Reconciliation.Overlap = TimeSpan.FromMinutes(30));
        var first = await h.Payments.Reconciliation.RunAsync(new ReconciliationRequest { Provider = "sim", ProviderAccount = Harness.Paybill }, Ct);
        h.Clock.Advance(TimeSpan.FromHours(2));

        var second = await h.Payments.Reconciliation.RunAsync(new ReconciliationRequest { Provider = "sim", ProviderAccount = Harness.Paybill }, Ct);

        Assert.Equal(first.To - TimeSpan.FromHours(48), first.From);
        Assert.Equal(first.To - TimeSpan.FromMinutes(30), second.From);
        Assert.Equal(h.Clock.Now, second.To);
    }

    [Fact]
    public async Task Resolving_a_discrepancy_closes_it_without_touching_the_ledger()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(300m, faults: SimulatedFaults.AmountMismatch);
        await h.DeliverAndProcessAsync();
        var discrepancy = Assert.Single((await h.ReconcileAsync()).NewDiscrepancies);

        var resolved = await h.TenantPayments.Discrepancies.ResolveAsync(discrepancy.DiscrepancyId, "Provider confirmed KES 300; statement typo.", Ct);

        Assert.NotNull(resolved.ResolvedAt);
        Assert.Empty(await h.TenantPayments.Discrepancies.ListOpenAsync(Ct));
        Assert.Empty((await h.ReconcileAsync()).NewDiscrepancies);   // not raised again
        Assert.Empty(await h.Payments.ForTenant(Harness.Other).Discrepancies.ListOpenAsync(Ct));
    }

    [Fact]
    public async Task An_imported_csv_statement_reconciles_like_the_providers_own()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m);
        await h.DeliverAndProcessAsync();
        var local = h.Clock.Now.ToOffset(TimeSpan.FromHours(3));
        string At(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var csv = string.Join("\r\n",
            "Merchant statement for 600123",
            "Receipt No.,Completion Time,Details,Paid In,Withdrawn,A/C No.,Linked Transaction ID",
            $"{paid.TransactionId},{At(local)},\"Pay Bill from 2547****0000, \"\"Jane\"\"\",300.00,,LN-1,",
            $"MISSED01,{At(local.AddMinutes(-10))},\"Pay Bill\n(multi-line details)\",\"1,250.00\",,LN-1,",
            $"PAYOUT01,{At(local.AddMinutes(-5))},Withdrawal to bank,,\"5,000.00\",,");
        var statement = await CsvStatementSource.ReadAsync("sim", Harness.Paybill, new StringReader(csv), new CsvStatementFormat
        {
            Currency = Currency.KES, TransactionId = "Receipt No.", ValueAt = "Completion Time", PaidIn = "Paid In", Withdrawn = "Withdrawn",
            AccountReference = "A/C No.", LinkedTransactionId = "Linked Transaction ID", TimeZone = "Africa/Nairobi", SkipLines = 1,
        }, Ct);

        var report = await h.ReconcileAsync(statement: statement);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(2, statement.Lines.Count);   // the payout is not a payment
        Assert.Equal(h.Clock.Now, statement.Lines[0].ValueAt);   // Nairobi wall time read as the right instant
        Assert.Equal(
            [ReconciliationClassification.Matched, ReconciliationClassification.MissingInEngine],
            report.Lines.Select(l => l.Classification).ToArray());
        Assert.Equal(Money.Kes(1_250m), (await h.PaymentsAsync()).Single(p => p.Source.Value == "MISSED01").Amount);
    }

    [Theory]
    [InlineData("Receipt No.,Completion Time,Paid In\nX1,not a time,10", "line 2")]
    [InlineData("Receipt No.,Completion Time,Paid In\nX1,2026-03-02 09:00:00,ten", "not an amount")]
    [InlineData("Receipt,Completion Time,Paid In\nX1,2026-03-02 09:00:00,10", "Receipt No.")]
    [InlineData("Receipt No.,Completion Time,Paid In\n\"X1,2026-03-02 09:00:00,10", "not closed")]
    public async Task A_malformed_statement_fails_before_anything_is_reconciled(string csv, string message)
    {
        var error = await Assert.ThrowsAsync<RuleViolationException>(() => CsvStatementSource.ReadAsync("sim", Harness.Paybill, new StringReader(csv), new CsvStatementFormat
        {
            Currency = Currency.KES, TransactionId = "Receipt No.", ValueAt = "Completion Time", PaidIn = "Paid In",
        }, Ct));

        Assert.Equal(PaymentsErrorCodes.InvalidStatement, error.ErrorCode);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_for_an_unmapped_account_is_unroutable_and_not_ingested()
    {
        var h = await Harness.CreateAsync();
        await h.Payments.Routing.RemoveAsync("sim", Harness.Paybill, Ct);
        h.PayBill(300m, faults: SimulatedFaults.Drop);

        var report = await h.ReconcileAsync();

        Assert.Equal(ReconciliationClassification.Unroutable, Assert.Single(report.Lines).Classification);
        Assert.Empty(await h.Payments.Inbox.ListAsync(InboxStatus.Pending, cancellationToken: Ct));
    }
}
