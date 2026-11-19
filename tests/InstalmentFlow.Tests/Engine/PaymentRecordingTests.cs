using CsCheck;

namespace InstalmentFlow.Tests;

public sealed class PaymentRecordingTests
{
    private const string Tenant = "acme-ke";
    private static readonly DateOnly s_today = new(2026, 11, 18);
    private static readonly DateOnly s_dec1 = new(2026, 12, 1);
    private static readonly DateTimeOffset s_valueAt = new(2026, 11, 18, 7, 30, 0, TimeSpan.Zero);

    private readonly FixedTimeProvider _time = FixedTimeProvider.AtNairobiDate(s_today);
    private readonly StubResolver _resolver = new();
    private readonly InstalmentFlowFactory _factory;

    public PaymentRecordingTests()
    {
        _factory = InstalmentFlowFactory.CreateInMemory(b => b
            .UseTimeProvider(_time)
            .UsePaymentReferenceResolver(_resolver)
            .AddTenant(Tenant, Currency.KES, "Africa/Nairobi", p => p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") })
            .AddTenant("other-ke", Currency.KES, "Africa/Nairobi", p => p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInstalmentFlow Flow => _factory.ForTenant(Tenant);

    // ----- Duplicates and idempotency -----

    [Fact]
    public async Task Scenario1_the_same_callback_five_times_records_one_payment()
    {
        var (_, _) = await CustomerWithLoan("C-1", "LN100001");
        var request = Paybill("TX1", 500m, "LN100001");

        var results = new List<RecordPaymentResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await Flow.Payments.RecordAsync(request, Ct));
        }

        Assert.Equal(RecordOutcome.HeldUnapplied, results[0].Outcome);
        Assert.All(results.Skip(1), r => Assert.Equal(RecordOutcome.Duplicate, r.Outcome));
        Assert.Single(results.Select(r => r.PaymentId).Distinct());
        Assert.Equal(Money.Kes(500m), (await Account("C-1")).Unapplied.Single());
        Invariants.AssertHold(_factory, Tenant);
    }

    [Fact]
    public async Task Same_provider_reference_with_different_keys_concurrently_records_once_I3()
    {
        await CustomerWithLoan("C-1", "LN100001");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            Flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001") with { IdempotencyKey = $"callback-{i}" }, Ct))));

        Assert.Equal(1, results.Count(r => r.Outcome == RecordOutcome.HeldUnapplied));
        Assert.Equal(9, results.Count(r => r.Outcome == RecordOutcome.Duplicate));
        Assert.Single(results.Select(r => r.PaymentId).Distinct());
        Assert.Equal(Money.Kes(500m), (await Account("C-1")).Unapplied.Single());
        Invariants.AssertHold(_factory, Tenant);
    }

    [Fact]
    public async Task Duplicates_landing_in_different_aggregates_still_record_once_I3()
    {
        // The same transaction notified with different account references: one copy matches the customer, the other
        // goes to unidentified funds. No shared aggregate version protects this; the store's uniqueness check must.
        await CustomerWithLoan("C-1", "LN100001");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            Flow.Payments.RecordAsync(Paybill("TX1", 500m, i % 2 == 0 ? "LN100001" : "typo") with { IdempotencyKey = $"callback-{i}" }, Ct))));

        Assert.Equal(19, results.Count(r => r.Outcome == RecordOutcome.Duplicate));
        Assert.Single(results.Select(r => r.PaymentId).Distinct());
        Assert.Single(_factory.InMemoryStore!.Snapshot(Tenant).Payments);
        Invariants.AssertHold(_factory, Tenant);
    }

    [Fact]
    public async Task Same_provider_reference_with_a_different_amount_is_rejected_IF1011()
    {
        await CustomerWithLoan("C-1", "LN100001");
        await Flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001"), Ct);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() =>
            Flow.Payments.RecordAsync(Paybill("TX1", 5_000m, "LN100001") with { IdempotencyKey = "other" }, Ct));

        Assert.Equal(ErrorCodes.PaymentConflict, ex.ErrorCode);
        Assert.Equal(Money.Kes(500m), (await Account("C-1")).Unapplied.Single());
    }

    [Fact]
    public async Task Same_idempotency_key_for_a_different_request_is_rejected_IF1002()
    {
        await CustomerWithLoan("C-1", "LN100001");
        await Flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001") with { IdempotencyKey = "k1" }, Ct);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() =>
            Flow.Payments.RecordAsync(Paybill("TX2", 500m, "LN100001") with { IdempotencyKey = "k1" }, Ct));
        var acrossCommands = await Assert.ThrowsAsync<RuleViolationException>(() =>
            Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "k1", ExternalId = "C-9" }, Ct));

        Assert.Equal(ErrorCodes.IdempotencyKeyReused, ex.ErrorCode);
        Assert.Equal(ErrorCodes.IdempotencyKeyReused, acrossCommands.ErrorCode);
    }

    [Fact]
    public async Task Retried_commands_replay_their_original_result()
    {
        var first = await Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-1", ExternalId = "C-1" }, Ct);
        var again = await Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-1", ExternalId = "C-1" }, Ct);

        Assert.Equal(first, again);
    }

    // ----- ADR-009 end to end -----

    [Fact]
    public async Task Paybill_with_exact_reference_is_directed_to_the_loan()
    {
        var (customer, loan) = await CustomerWithLoan("C-1", "LN100001");

        var result = await Flow.Payments.RecordAsync(Paybill("TX1", 150m, " ln 100001 "), Ct);

        Assert.Equal((RecordOutcome.HeldUnapplied, customer, loan), (result.Outcome, result.AccountId!.Value, result.IdentifiedObligation!.Value));
        Assert.Null(result.Exception);
        Assert.Equal(Money.Kes(150m), result.UnappliedAfter);
    }

    [Fact]
    public async Task Stk_push_with_known_obligation_is_directed()
    {
        var (_, loan) = await CustomerWithLoan("C-1", "LN100001");

        var result = await Flow.Payments.RecordAsync(Request("TX1", 500m, PaymentTarget.ForObligation(loan)), Ct);

        Assert.Equal(loan, result.IdentifiedObligation);
    }

    [Fact]
    public async Task Unknown_or_invalid_references_become_unidentified_funds_with_an_exception()
    {
        await CustomerWithLoan("C-1", "LN100001");

        var unknown = await Flow.Payments.RecordAsync(Paybill("TX1", 300m, "LN999999"), Ct);
        var invalid = await Flow.Payments.RecordAsync(Paybill("TX2", 200m, "my loan"), Ct);
        var empty = await Flow.Payments.RecordAsync(Paybill("TX3", 100m, ""), Ct);
        var nothing = await Flow.Payments.RecordAsync(Request("TX4", 50m, PaymentTarget.Unknown), Ct);
        var missingObligation = await Flow.Payments.RecordAsync(Request("TX5", 25m, PaymentTarget.ForObligation(new ObligationId(Guid.CreateVersion7()))), Ct);

        Assert.All([unknown, invalid, empty, nothing, missingObligation], r =>
        {
            Assert.Equal(RecordOutcome.Unidentified, r.Outcome);
            Assert.Null(r.AccountId);
            Assert.Equal(PaymentExceptionReason.UnidentifiedPayment, r.Exception!.Reason);
        });
        Assert.Equal("my loan", invalid.Exception!.RawReference);
        Assert.Equal(5, await OpenExceptions().CountAsync(Ct));
        Invariants.AssertHold(_factory, Tenant);
    }

    [Fact]
    public async Task Customer_with_two_loans_and_no_reference_is_held_and_needs_an_instruction()
    {
        var (customer, _) = await CustomerWithLoan("C-1", "LN100001");
        await Loan(customer, "loan-2", "LN100002");

        var result = await Flow.Payments.RecordAsync(Request("TX1", 400m, PaymentTarget.ForCustomer(customer)), Ct);

        Assert.Equal(RecordOutcome.HeldUnapplied, result.Outcome);
        Assert.Null(result.IdentifiedObligation);
        Assert.Equal(PaymentExceptionReason.AllocationInstructionRequired, result.Exception!.Reason);
        Assert.Equal(2, result.Exception.Candidates.Count);
        Assert.Equal(customer, result.Exception.HeldBy);
    }

    [Fact]
    public async Task Ambiguous_reference_via_resolver_across_customers_is_unidentified()
    {
        var (_, loan1) = await CustomerWithLoan("C-1", "LN100001");
        var (_, loan2) = await CustomerWithLoan("C-2", "LN100002");
        _resolver.Map("OLD-10000", loan1, loan2);

        var result = await Flow.Payments.RecordAsync(Paybill("TX1", 500m, "OLD-10000"), Ct);

        Assert.Equal(RecordOutcome.Unidentified, result.Outcome);
        Assert.Equal(PaymentExceptionReason.AmbiguousReference, result.Exception!.Reason);
        // IDs from a frozen test clock share a timestamp, so compare as sets.
        Assert.Equal(new[] { loan1, loan2 }.OrderBy(id => id.Value), result.Exception.Candidates.Select(c => c.ObligationId!.Value).OrderBy(id => id.Value));
    }

    [Fact]
    public async Task Resolver_cannot_bypass_ambiguity_when_an_exact_match_exists()
    {
        var (_, loan1) = await CustomerWithLoan("C-1", "LN100001");
        var (_, loan2) = await CustomerWithLoan("C-2", "LN100002");
        _resolver.Map("LN100001", loan2);

        var result = await Flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001"), Ct);

        Assert.Equal(PaymentExceptionReason.AmbiguousReference, result.Exception!.Reason);
        Assert.Null(result.IdentifiedObligation);
        Assert.Contains(result.Exception.Candidates, c => c.ObligationId == loan1);
    }

    [Fact]
    public async Task Currency_mismatch_and_no_payable_obligation()
    {
        var (_, loan) = await CustomerWithLoan("C-1", "LN100001");
        var bare = await OpenAccount("C-2");

        var usd = await Flow.Payments.RecordAsync(Request("TX1", 10m, PaymentTarget.ForObligation(loan), Currency.USD), Ct);
        var noLoan = await Flow.Payments.RecordAsync(Request("TX2", 10m, PaymentTarget.ForCustomer(bare)), Ct);

        Assert.Equal((RecordOutcome.Unidentified, PaymentExceptionReason.CurrencyMismatch), (usd.Outcome, usd.Exception!.Reason));
        Assert.Equal((RecordOutcome.HeldUnapplied, PaymentExceptionReason.NoPayableObligation), (noLoan.Outcome, noLoan.Exception!.Reason));
        Invariants.AssertHold(_factory, Tenant);
    }

    // ----- Exception queue -----

    [Fact]
    public async Task Unidentified_funds_assigned_to_the_customer_move_through_the_journal()
    {
        var (customer, loan) = await CustomerWithLoan("C-1", "LN100001");
        var payment = await Flow.Payments.RecordAsync(Paybill("TX1", 300m, "LN10001"), Ct);   // typo

        var resolution = await Flow.Exceptions.AssignToCustomerAsync(new AssignToCustomerRequest
        {
            IdempotencyKey = "assign-1",
            ExceptionId = payment.Exception!.ExceptionId,
            Account = customer,
            Obligation = loan,
            ResolvedBy = "ops@acme",
            Note = "Customer confirmed by phone",
        }, Ct);

        Assert.Equal(PaymentExceptionStatus.Resolved, resolution.Exception.Status);
        Assert.Equal(ExceptionResolutionKind.AssignedToCustomer, resolution.Exception.Resolution!.Kind);
        Assert.Equal(Money.Kes(300m), resolution.UnappliedAfter);
        var view = await Flow.Queries.GetPaymentAsync(payment.PaymentId, Ct);
        Assert.Equal((customer, loan, Money.Kes(300m)), (view.AccountId!.Value, view.IdentifiedObligation!.Value, view.Unapplied));
        Assert.Empty(await OpenExceptions().ToListAsync(Ct));
        Invariants.AssertHold(_factory, Tenant);

        var twice = await Assert.ThrowsAsync<RuleViolationException>(() => Flow.Exceptions.AssignToCustomerAsync(new AssignToCustomerRequest
        {
            IdempotencyKey = "assign-2", ExceptionId = payment.Exception.ExceptionId, Account = customer, ResolvedBy = "ops@acme",
        }, Ct));
        Assert.Equal(ErrorCodes.ExceptionNotResolvable, twice.ErrorCode);
    }

    [Fact]
    public async Task Unidentified_funds_cannot_be_dismissed_I22_but_customer_held_ones_can()
    {
        var (customer, _) = await CustomerWithLoan("C-1", "LN100001");
        await Loan(customer, "loan-2", "LN100002");
        var unidentified = await Flow.Payments.RecordAsync(Paybill("TX1", 300m, "nope"), Ct);
        var held = await Flow.Payments.RecordAsync(Request("TX2", 400m, PaymentTarget.ForCustomer(customer)), Ct);

        var blocked = await Assert.ThrowsAsync<RuleViolationException>(() => Flow.Exceptions.DismissAsync(Dismiss(unidentified.Exception!.ExceptionId), Ct));
        var dismissed = await Flow.Exceptions.DismissAsync(Dismiss(held.Exception!.ExceptionId), Ct);

        Assert.Equal(ErrorCodes.ExceptionNotResolvable, blocked.ErrorCode);
        Assert.Equal(PaymentExceptionStatus.Dismissed, dismissed.Exception.Status);
        Assert.Equal("Customer wants it kept as credit", dismissed.Exception.Resolution!.Note);
        Assert.Equal(Money.Kes(400m), dismissed.UnappliedAfter);
        Invariants.AssertHold(_factory, Tenant);
    }

    [Fact]
    public async Task Open_exceptions_can_be_filtered_by_reason()
    {
        var (customer, _) = await CustomerWithLoan("C-1", "LN100001");
        await Loan(customer, "loan-2", "LN100002");
        await Flow.Payments.RecordAsync(Paybill("TX1", 300m, "nope"), Ct);
        await Flow.Payments.RecordAsync(Request("TX2", 400m, PaymentTarget.ForCustomer(customer)), Ct);

        var unidentified = await OpenExceptions(new ExceptionFilter { Reason = PaymentExceptionReason.UnidentifiedPayment }).ToListAsync(Ct);

        Assert.Equal(300m, Assert.Single(unidentified).Amount.Amount);
        Assert.Equal(2, await OpenExceptions().CountAsync(Ct));
        Assert.Empty(await OpenExceptions(new ExceptionFilter { OpenedBefore = _time.Now.AddMinutes(-1) }).ToListAsync(Ct));
    }

    // ----- Tenancy, concurrency and properties -----

    [Fact]
    public async Task Payments_are_isolated_per_tenant_I18()
    {
        await CustomerWithLoan("C-1", "LN100001");
        var payment = await Flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001"), Ct);
        var other = _factory.ForTenant("other-ke");

        await Assert.ThrowsAsync<NotFoundException>(() => other.Queries.GetPaymentAsync(payment.PaymentId, Ct));
        var sameReferenceElsewhere = await other.Payments.RecordAsync(Paybill("TX1", 500m, "LN100001"), Ct);

        Assert.Equal(RecordOutcome.Unidentified, sameReferenceElsewhere.Outcome);
        Assert.NotEqual(payment.PaymentId, sameReferenceElsewhere.PaymentId);
        Invariants.AssertHold(_factory, Tenant);
        Invariants.AssertHold(_factory, "other-ke");
    }

    [Fact]
    public async Task Concurrent_payments_for_one_customer_all_land_via_retries()
    {
        // 20 simultaneous payments for one customer is far beyond real contention; allow enough retries that the
        // test cannot fail by chance.
        var factory = InstalmentFlowFactory.CreateInMemory(b => b
            .UseTimeProvider(_time)
            .UseConcurrencyRetries(100)
            .AddTenant(Tenant, Currency.KES, "Africa/Nairobi", p => p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
        var flow = factory.ForTenant(Tenant);
        var customer = (await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "a", ExternalId = "C-1" }, Ct)).AccountId;
        await flow.Obligations.OpenAsync(LoanRequest(customer, "loan-1", "LN100001"), Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            flow.Payments.RecordAsync(Paybill($"TX{i}", 100m + i, "LN100001"), Ct))));

        Assert.All(results, r => Assert.Equal(RecordOutcome.HeldUnapplied, r.Outcome));
        var summary = await flow.Queries.GetAccountAsync(customer, cancellationToken: Ct);
        Assert.Equal(Money.Kes(Enumerable.Range(0, 20).Sum(i => 100m + i)), summary.Unapplied.Single());
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public void Property_any_sequence_of_payments_and_retries_keeps_every_invariant_I19()
    {
        var commandGen =
            from source in Gen.Int[1, 12]
            from amount in Gen.Int[1, 2_000]
            from kind in Gen.Int[0, 4]
            from retry in Gen.Bool
            select (Source: $"TX{source}", Amount: (decimal)amount, Kind: kind, Retry: retry);

        commandGen.Array[1, 30].Sample(commands =>
        {
            var factory = InstalmentFlowFactory.CreateInMemory(b => b
                .UseTimeProvider(_time)
                .AddTenant(Tenant, Currency.KES, "Africa/Nairobi", p => p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
            var flow = factory.ForTenant(Tenant);
            var c1 = flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "a1", ExternalId = "C-1" }).GetAwaiter().GetResult().AccountId;
            var c2 = flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "a2", ExternalId = "C-2" }).GetAwaiter().GetResult().AccountId;
            flow.Obligations.OpenAsync(LoanRequest(c1, "loan-1", "LN100001")).GetAwaiter().GetResult();
            flow.Obligations.OpenAsync(LoanRequest(c1, "loan-2", "LN100002")).GetAwaiter().GetResult();

            var recorded = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var command in commands)
            {
                var target = command.Kind switch
                {
                    0 => PaymentTarget.ForReference("LN100001"),
                    1 => PaymentTarget.ForReference("garbage"),
                    2 => PaymentTarget.ForCustomer(c1),
                    3 => PaymentTarget.ForCustomer(c2),
                    _ => PaymentTarget.Unknown,
                };
                var request = Request(command.Source, command.Amount, target) with { IdempotencyKey = $"{command.Source}:{command.Amount}:{command.Kind}" };
                try
                {
                    flow.Payments.RecordAsync(request).GetAwaiter().GetResult();
                    if (command.Retry)
                    {
                        Assert.Equal(RecordOutcome.Duplicate, flow.Payments.RecordAsync(request).GetAwaiter().GetResult().Outcome);
                    }

                    recorded.TryAdd(command.Source, command.Amount);
                }
                catch (RuleViolationException ex) when (ex.ErrorCode == ErrorCodes.PaymentConflict)
                {
                    Assert.NotEqual(command.Amount, recorded[command.Source]);
                }
            }

            var snapshot = factory.InMemoryStore!.Snapshot(Tenant);
            Assert.Equal(recorded.Count, snapshot.Payments.Length);
            Assert.Equal(recorded.Values.Sum(), snapshot.Payments.Sum(p => p.Amount.Amount));
            var violations = InstalmentFlow.Verification.LedgerVerifier.Verify(snapshot);
            Assert.True(violations.Count == 0, string.Join('\n', violations));
        }, iter: 200);
    }

    // ----- Helpers -----

    private async Task<(AccountId Customer, ObligationId Loan)> CustomerWithLoan(string customerId, string reference)
    {
        var customer = await OpenAccount(customerId);
        var loan = await Loan(customer, $"loan-{reference}", reference);
        return (customer, loan);
    }

    private async Task<AccountId> OpenAccount(string externalId) =>
        (await Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{externalId}", ExternalId = externalId }, Ct)).AccountId;

    private async Task<ObligationId> Loan(AccountId customer, string externalId, string reference) =>
        (await Flow.Obligations.OpenAsync(LoanRequest(customer, externalId, reference), Ct)).Obligation.ObligationId;

    private Task<AccountSummary> Account(string externalId) => Flow.Queries.GetAccountAsync(AccountRef.ByExternalId(externalId), cancellationToken: Ct);

    private IAsyncEnumerable<PaymentExceptionView> OpenExceptions(ExceptionFilter? filter = null) => Flow.Exceptions.ListOpenAsync(filter, Ct);

    private static OpenObligationRequest LoanRequest(AccountId customer, string externalId, string reference) => new()
    {
        IdempotencyKey = $"open-{externalId}",
        Account = customer,
        ExternalId = externalId,
        PaymentReference = reference,
        Components = [Component.Principal(Money.Kes(15_000m))],
        Plan = PlanSpec.Equal(Frequency.Daily, 30, s_dec1),
    };

    private static RecordPaymentRequest Paybill(string transId, decimal amount, string billRef) =>
        Request(transId, amount, PaymentTarget.ForReference(billRef));

    private static RecordPaymentRequest Request(string transId, decimal amount, PaymentTarget target, Currency? currency = null) => new()
    {
        IdempotencyKey = $"mpesa:600123:{transId}",
        Source = new ExternalReference("mpesa:600123", transId),
        Amount = new Money(amount, currency ?? Currency.KES),
        ValueAt = s_valueAt,
        Target = target,
        Metadata = new Dictionary<string, string> { ["MSISDN"] = "2547 ***** 126" },
    };

    private static DismissExceptionRequest Dismiss(PaymentExceptionId id) => new()
    {
        IdempotencyKey = $"dismiss-{id}",
        ExceptionId = id,
        Reason = "Customer wants it kept as credit",
        ResolvedBy = "ops@acme",
    };

    private sealed class StubResolver : IPaymentReferenceResolver
    {
        private readonly Dictionary<string, ObligationId[]> _map = new(StringComparer.Ordinal);

        public void Map(string raw, params ObligationId[] obligations) => _map[raw] = obligations;

        public ValueTask<IReadOnlyList<ReferenceCandidate>> ResolveAsync(ReferenceResolutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReferenceCandidate>>(
                _map.TryGetValue(context.RawReference, out var ids) ? [.. ids.Select(id => ReferenceCandidate.ForObligation(id))] : []);
    }
}
