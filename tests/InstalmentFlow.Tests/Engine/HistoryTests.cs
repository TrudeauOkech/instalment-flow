using System.Globalization;
using System.Text;
using CsCheck;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Tests;

/// <summary>History (M6): as-of reconstruction by journal fold, statements, payment traces, journal export and Verify.</summary>
public sealed class HistoryTests
{
    private static readonly DateOnly s_today = Flows.Today;

    private static CancellationToken Ct => Flows.Ct;

    // ----- As-of -----

    [Fact]
    public async Task A_past_query_is_unaffected_by_later_commands()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 300m, 300m);
        var t0 = f.Time.Now;
        var before = await Describe(f, customer);

        Tick(f);
        await f.PayAsync("TX1", 450m, "LN100001");
        var t1 = f.Time.Now;
        var afterPayment = await Describe(f, customer);

        Tick(f);
        await f.ReverseAsync("TX1", 200m);
        await f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "c", Obligation = ObligationRef.ByReference("LN100001"), Type = ChargeType.Penalty, Amount = Money.Kes(25m), Description = "Late",
        }, Ct);
        Tick(f);

        Assert.Equal(before, await Describe(f, customer, AsOf.At(t0)));
        Assert.Equal(afterPayment, await Describe(f, customer, AsOf.At(t1)));
        Assert.NotEqual(afterPayment, await Describe(f, customer));
    }

    [Fact]
    public async Task As_of_a_business_date_includes_that_whole_day_and_evaluates_status_for_it()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m, 100m);
        await f.PayAsync("TX1", 100m, "LN100001");
        f.Time.Now = f.Time.Now.AddDays(3);
        await f.PayAsync("TX2", 100m, "LN100001");

        var then = await f.Flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), s_today.AddDays(1), Ct);
        var now = await f.LoanViewAsync("LN100001");

        Assert.Equal((Money.Kes(100m), InstalmentTiming.Due), (then.Outstanding, then.Instalments[1].Timing));
        Assert.Equal((Money.Kes(0m), ObligationStatus.Settled), (now.Outstanding, now.Status));
    }

    [Fact]
    public async Task Objects_that_did_not_exist_yet_are_not_found_as_of_then()
    {
        var f = new Flows();
        var t0 = f.Time.Now;
        Tick(f);
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Flow.Queries.GetAccountAsync(customer, AsOf.At(t0), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), AsOf.At(t0), Ct));
    }

    [Fact]
    public async Task As_of_shows_the_plan_version_and_cancellation_in_force_then()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(5), 500m);
        var t0 = f.Time.Now;
        Tick(f);
        await f.Flow.Plans.SupersedeAsync(new SupersedePlanRequest
        {
            IdempotencyKey = "s", Obligation = ObligationRef.ByReference("LN100001"), Reason = "Typo",
            Plan = PlanSpec.Custom([new PlannedInstalment(s_today.AddDays(6), [Component.Principal(Money.Kes(500m))])]),
        }, Ct);
        var t1 = f.Time.Now;
        Tick(f);
        await f.Flow.Obligations.CancelAsync(new CancelObligationRequest
        {
            IdempotencyKey = "x", Obligation = ObligationRef.ByReference("LN100001"), Reason = "Returned", WriteOffs = [Component.Principal(Money.Kes(500m))],
        }, Ct);

        var atOpen = await f.Flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), AsOf.At(t0), Ct);
        var atSupersede = await f.Flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), AsOf.At(t1), Ct);
        var now = await f.LoanViewAsync("LN100001");

        Assert.Equal((1, ObligationStatus.Active, Money.Kes(500m)), (atOpen.ActivePlanVersion!.Value, atOpen.Status, atOpen.Outstanding));
        Assert.Equal((2, ObligationStatus.Active, Money.Kes(500m)), (atSupersede.ActivePlanVersion!.Value, atSupersede.Status, atSupersede.Outstanding));
        Assert.Equal((ObligationStatus.Cancelled, Money.Kes(0m)), (now.Status, now.Outstanding));
    }

    [Fact]
    public async Task An_obligation_opened_after_the_cut_off_does_not_exist_as_of_then()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        var t0 = f.Time.Now;
        Tick(f);
        await f.LoanAsync(customer, "LN100001", s_today, 100m);

        var then = await f.Flow.Queries.GetAccountAsync(customer, AsOf.At(t0), Ct);

        Assert.Empty(then.Obligations);
        await Assert.ThrowsAsync<NotFoundException>(() => f.Flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), AsOf.At(t0), Ct));
    }

    [Fact]
    public async Task The_fold_reproduces_rare_paths_assigned_funds_and_a_failed_refund_that_settles_a_deficit()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);

        // Unidentified funds, partly reversed while in the pool, then assigned to the customer.
        var unidentified = await f.PayAsync("TX0", 300m, "WRONG");
        Tick(f);
        await f.ReverseAsync("TX0", 100m);
        Tick(f);
        await f.Flow.Exceptions.AssignToCustomerAsync(new AssignToCustomerRequest
        {
            IdempotencyKey = "assign", ExceptionId = unidentified.Exception!.ExceptionId, Account = customer, ResolvedBy = "ops",
        }, Ct);
        AssertFoldMatchesProjection(f, customer);

        // A refund pending while its payment is reversed, then failing: it settles the deficit.
        Tick(f);
        await f.PayAsync("TX1", 700m, "LN100001");
        var refund = await f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = "r", Account = customer, Amount = Money.Kes(400m), Reason = "Overpayment", RequestedBy = "ops",
        }, Ct);
        Tick(f);
        await f.ReverseAsync("TX1");
        var beforeFailure = f.Time.Now;
        var stateBefore = await Describe(f, customer);
        Tick(f);
        await f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest
        {
            IdempotencyKey = "fail", Refund = refund.Refund.RefundId, Completion = RefundCompletion.Failed, FailureReason = "Wallet closed",
        }, Ct);

        AssertFoldMatchesProjection(f, customer);
        Assert.Equal(stateBefore, await Describe(f, customer, AsOf.At(beforeFailure)));
        f.AssertInvariants();
    }

    /// <summary>
    /// Random histories: after every command the invariants hold (Verify), and the history fold at "now" reproduces
    /// the live projections (I-10 at the aggregate level). At the end, every earlier state is reproduced as of its
    /// time. Set <c>IF_HISTORIES</c> to run more (the release check runs 10⁵).
    /// </summary>
    [Fact]
    public void Property_random_histories_fold_to_their_projections_and_reproduce_every_past_state()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("IF_HISTORIES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 150;
        var command = Gen.Select(Gen.Int[0, 11], Gen.Int[1, 600], Gen.Int[0, 9], (kind, amount, pick) => (kind, amount, pick));
        command.Array[1, 25].Sample(commands => RunHistory(commands), iter: iterations);
    }

    private static void RunHistory((int Kind, int Amount, int Pick)[] commands)
    {
        var f = new Flows(p => p with { MultipleObligations = MultipleObligationsPolicy.AllocateByStrategy });
        var customer = f.CustomerAsync("C-1").GetAwaiter().GetResult();
        f.LoanAsync(customer, "LN100001", s_today.AddDays(-2), 200m, 200m, 200m).GetAwaiter().GetResult();
        f.LoanAsync(customer, "LN100002", s_today, 300m, 300m).GetAwaiter().GetResult();
        var states = new List<(DateTimeOffset At, string State)>();
        var payments = new List<string>();
        var charges = new List<ChargeId>();
        var refunds = new List<RefundId>();
        var step = 0;

        foreach (var (kind, amount, pick) in commands)
        {
            step++;
            Tick(f);
            var loan = pick % 2 == 0 ? "LN100001" : "LN100002";
            try
            {
                switch (kind)
                {
                    case 0 or 1 or 2:
                        f.PayAsync($"TX{step}", amount, loan).GetAwaiter().GetResult();
                        payments.Add($"TX{step}");
                        break;
                    case 3:
                        f.Flow.Payments.RecordAsync(Flows.Request($"TX{step}", amount, PaymentTarget.ForCustomer(customer)), Ct).GetAwaiter().GetResult();
                        payments.Add($"TX{step}");
                        break;
                    case 4 when payments.Count > 0:
                        f.ReverseAsync(payments[pick % payments.Count], pick < 5 ? null : amount / 3m, key: $"rev-{step}").GetAwaiter().GetResult();
                        break;
                    case 5:
                        charges.Add(f.Flow.Charges.PostAsync(new PostChargeRequest
                        {
                            IdempotencyKey = $"charge-{step}", Obligation = ObligationRef.ByReference(loan), Type = pick < 5 ? ChargeType.Penalty : ChargeType.Fee,
                            Amount = Money.Kes(amount / 10m), Description = "Charge", DuePolicy = pick < 3 ? ChargeDue.Immediately : ChargeDue.PerTenantPolicy,
                        }, Ct).GetAwaiter().GetResult().ChargeId);
                        break;
                    case 6 when charges.Count > 0:
                        f.Flow.Charges.ReverseAsync(new ReverseChargeRequest { IdempotencyKey = $"rc-{step}", Charge = charges[pick % charges.Count], Reason = "Error" }, Ct).GetAwaiter().GetResult();
                        break;
                    case 7:
                        f.Flow.Charges.WaiveAsync(new WaiveRequest
                        {
                            IdempotencyKey = $"waive-{step}", Obligation = ObligationRef.ByReference(loan), Component = ComponentKey.Principal, Amount = Money.Kes(amount / 20m), Reason = "Goodwill",
                        }, Ct).GetAwaiter().GetResult();
                        break;
                    case 8:
                        refunds.Add(f.Flow.Refunds.RequestAsync(new RequestRefundRequest
                        {
                            IdempotencyKey = $"refund-{step}", Account = customer, Amount = Money.Kes(amount / 4m), Reason = "Overpayment", RequestedBy = "ops",
                        }, Ct).GetAwaiter().GetResult().Refund.RefundId);
                        break;
                    case 9 when refunds.Count > 0:
                        f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest
                        {
                            IdempotencyKey = $"complete-{step}", Refund = refunds[pick % refunds.Count], Completion = pick < 5 ? RefundCompletion.Confirmed : RefundCompletion.Failed,
                            FailureReason = "Wallet closed",
                        }, Ct).GetAwaiter().GetResult();
                        break;
                    case 10:
                        f.Flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = $"sweep-{step}", Account = customer, AllowAcrossObligations = pick < 5 }, Ct).GetAwaiter().GetResult();
                        break;
                    default:
                        f.Time.Now = f.Time.Now.AddDays(1);
                        break;
                }
            }
            catch (InstalmentFlowException)
            {
                // Generated commands may be invalid (too large, already reversed…); the rules reject them and nothing changes.
            }

            f.AssertInvariants();
            AssertFoldMatchesProjection(f, customer);
            states.Add((f.Time.Now, Describe(f, customer).GetAwaiter().GetResult()));
        }

        Tick(f);
        foreach (var (at, state) in states)
        {
            Assert.Equal(state, Describe(f, customer, AsOf.At(at)).GetAwaiter().GetResult());
        }
    }

    private static void AssertFoldMatchesProjection(Flows f, AccountId customer)
    {
        var store = f.Factory.InMemoryStore!;
        var current = store.LoadAccountAsync(Flows.Tenant, customer, Ct).AsTask().GetAwaiter().GetResult()!;
        var history = store.LoadHistoryAsync(Flows.Tenant, customer, Ct).AsTask().GetAwaiter().GetResult();
        var folded = HistoryFold.At(current, history, f.Time.Now);
        Assert.Equal(Canonical(current), Canonical(folded));
    }

    private static string Canonical(CustomerAccount account)
    {
        var text = new StringBuilder();
        foreach (var lot in account.Lots)
        {
            text.Append(CultureInfo.InvariantCulture, $"lot {lot.PaymentId} {lot.Original} rem {lot.Remaining} rev {lot.Reversed} ref {lot.Refunded} def {lot.Deficit}\n");
        }

        foreach (var obligation in account.Obligations)
        {
            text.Append(CultureInfo.InvariantCulture, $"obl {obligation.PaymentReference} {obligation.Status}\n");
            foreach (var plan in obligation.Plans)
            {
                text.Append(CultureInfo.InvariantCulture, $" v{plan.Version} {plan.Status}\n");
                foreach (var instalment in plan.Instalments)
                {
                    foreach (var c in instalment.Components)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"  #{instalment.Sequence} {c.Key} due {c.Due} alloc {c.Allocated} waived {c.Waived} charged {c.Charged}\n");
                    }
                }
            }
        }

        return text.ToString();
    }

    // ----- Statements -----

    [Fact]
    public async Task A_statement_lists_each_event_with_a_running_balance()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        await f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = "r", Account = customer, Amount = Money.Kes(200m), Reason = "Overpayment", RequestedBy = "ops",
        }, Ct);
        f.Time.Now = f.Time.Now.AddDays(1);
        await f.ReverseAsync("TX1");

        var lines = await f.Flow.Queries.GetStatementAsync(customer, s_today, s_today.AddDays(1), Ct).ToListAsync(Ct);

        Assert.Equal(
            [StatementEntryKind.ObligationOpened, StatementEntryKind.PaymentReceived, StatementEntryKind.RefundRequested, StatementEntryKind.PaymentReversed],
            lines.Select(l => l.Kind));
        Assert.Equal([500m, 700m, 200m, 700m], lines.Select(l => l.Amount.Amount));
        Assert.Equal([0m, 500m, 0m, 0m], lines.Select(l => l.Allocated.Amount));
        Assert.Equal([500m, -200m, 0m, 700m], lines.Select(l => l.Balance.Amount));   // owes 500 + 200 reversal receivable
        Assert.Contains("mpesa:600123", lines[1].Description, StringComparison.Ordinal);

        var later = await f.Flow.Queries.GetStatementAsync(customer, s_today.AddDays(1), s_today.AddDays(1), Ct).ToListAsync(Ct);
        Assert.Equal((Money.Kes(700m), Money.Kes(700m)), (Assert.Single(later).Change, later[0].Balance));   // opening balance carried in
    }

    // ----- Payment trace -----

    [Fact]
    public async Task A_payment_trace_reconciles_every_shilling()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 300m, 300m);
        await f.PayAsync("TX1", 1_000m, "LN100001");   // #1 300, held 700
        Tick(f);
        var refund = await f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = "r", Account = customer, Amount = Money.Kes(100m), Reason = "Asked", RequestedBy = "ops",
        }, Ct);
        Tick(f);
        await f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest { IdempotencyKey = "c", Refund = refund.Refund.RefundId, Completion = RefundCompletion.Confirmed }, Ct);
        Tick(f);
        await f.ReverseAsync("TX1", 800m);   // 600 unapplied + 200 unwound

        var trace = await f.Flow.Queries.TracePaymentAsync(PaymentRef.BySource(Flows.Source("TX1")), Ct);

        Assert.Equal(
            [PaymentTraceStepKind.Received, PaymentTraceStepKind.Allocated, PaymentTraceStepKind.RefundRequested, PaymentTraceStepKind.RefundConfirmed,
             PaymentTraceStepKind.AllocationReversed, PaymentTraceStepKind.Reversed],
            trace.Steps.Select(s => s.Kind));
        var p = trace.Payment;
        Assert.Equal((100m, 0m, 800m, 100m), (p.Allocated.Amount, p.Unapplied.Amount, p.Reversed.Amount, p.Refunded.Amount));
        Assert.Equal(p.Amount, p.Allocated + p.Unapplied + (p.Reversed - trace.Deficit) + p.Refunded);
    }

    // ----- Journal export -----

    [Fact]
    public async Task The_journal_exports_in_order_and_resumes_from_a_cursor()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 200m, "LN100001");

        var first = await f.Flow.Queries.ReadJournalAsync(JournalCursor.Start, Ct).ToListAsync(Ct);
        await f.PayAsync("TX2", 100m, "LN100001");
        var next = await f.Flow.Queries.ReadJournalAsync(first[^1].Cursor, Ct).ToListAsync(Ct);

        Assert.Equal(["ObligationOpened", "PaymentReceived"], first.Select(t => t.Kind));
        Assert.Equal([1L, 2L], first.Select(t => t.Cursor.Position));
        Assert.Equal(3L, Assert.Single(next).Cursor.Position);
        Assert.All(first.Concat(next), t => Assert.Equal(t.Lines.Sum(l => l.Debit), t.Lines.Sum(l => l.Credit)));
        Assert.Contains(next[0].Lines, l => l.Account == "clearing:mpesa:600123" && l.Debit == 100m && l.SourceType == "Payment");
    }

    [Fact]
    public async Task Large_journals_are_read_across_pages()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(30), 100_000m);
        for (var i = 0; i < 1_200; i++)
        {
            await f.PayAsync($"TX{i}", 1m, "LN100001");
        }

        var all = await f.Flow.Queries.ReadJournalAsync(JournalCursor.Start, Ct).ToListAsync(Ct);

        Assert.Equal(1_201, all.Count);
        Assert.Equal(Enumerable.Range(1, 1_201).Select(i => (long)i), all.Select(t => t.Cursor.Position));
    }

    // ----- Verify -----

    [Fact]
    public async Task Verify_passes_on_a_healthy_tenant_and_reports_a_broken_invariant()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");

        var healthy = await f.Flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct);
        Assert.True(healthy.Passed);
        Assert.Equal((2, 1, 1), (healthy.JournalTransactions, healthy.Payments, healthy.Accounts));

        // Tamper: an unbalanced transaction written behind the engine's back.
        var tampered = new JournalTransaction(new JournalTxId(Guid.CreateVersion7()), Flows.Tenant, JournalKind.FundsAllocated, customer, "tamper", s_today, f.Time.Now,
            [new LedgerLine(LedgerAccounts.Unapplied(customer, Currency.KES), Currency.KES, 50m, 0m, LedgerSourceType.Allocation, Guid.NewGuid()),
             new LedgerLine("obl:x:principal", Currency.KES, 0m, 40m, LedgerSourceType.Allocation, Guid.NewGuid())]);
        await f.Factory.InMemoryStore!.CommitAsync(new Commit { Tenant = Flows.Tenant, Journal = tampered }, Ct);

        var broken = await f.Flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct);
        Assert.False(broken.Passed);
        Assert.Contains(broken.Violations, v => v.Invariant == "I-1");
        Assert.Contains(broken.Violations, v => v.Invariant == "I-10");
    }

    // ----- Helpers -----

    private static void Tick(Flows f) => f.Time.Now = f.Time.Now.AddMinutes(1);

    /// <summary>A canonical text form of everything the queries show for the customer, so states compare structurally.</summary>
    private static async Task<string> Describe(Flows f, AccountId customer, AsOf? asOf = null)
    {
        var account = await f.Flow.Queries.GetAccountAsync(customer, asOf, Ct);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"unapplied {string.Join(',', account.Unapplied)} receivable {string.Join(',', account.ReversalReceivable)}\n");
        foreach (var summary in account.Obligations)
        {
            var o = await f.Flow.Queries.GetObligationAsync(summary.ObligationId, asOf, Ct);
            text.Append(CultureInfo.InvariantCulture, $"{o.PaymentReference} {o.Status} outstanding {o.Outstanding} arrears {o.Arrears} dpd {o.DaysPastDue} paid-through {o.PaidThroughDate} v{o.ActivePlanVersion}\n");
            foreach (var c in o.Components)
            {
                text.Append(CultureInfo.InvariantCulture, $" {c.Key} {c.Originated} {c.Paid} {c.Waived} {c.Outstanding}\n");
            }

            foreach (var i in o.Instalments)
            {
                text.Append(CultureInfo.InvariantCulture, $" #{i.Sequence} {i.Due} {i.Paid} {i.Waived} {i.PaymentState} {i.Timing}\n");
            }
        }

        return text.ToString();
    }
}
