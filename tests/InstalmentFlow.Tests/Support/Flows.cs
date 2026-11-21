namespace InstalmentFlow.Tests;

/// <summary>Shared set-up for end-to-end tests against the in-memory engine: tenant <c>acme-ke</c>, KES, Nairobi time.</summary>
internal sealed class Flows
{
    public const string Tenant = "acme-ke";
    public static readonly DateOnly Today = new(2026, 12, 1);

    public Flows(Func<TenantPolicy, TenantPolicy>? policy = null, Action<InstalmentFlowBuilder>? configure = null)
    {
        Factory = InstalmentFlowFactory.CreateInMemory(b =>
        {
            b.UseTimeProvider(Time).UseConcurrencyRetries(100);
            configure?.Invoke(b);
            b.AddTenant(Tenant, Currency.KES, "Africa/Nairobi", p => (policy ?? (x => x))(p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
        });
        Flow = Factory.ForTenant(Tenant);
    }

    public FixedTimeProvider Time { get; } = FixedTimeProvider.AtNairobiDate(Today);

    public InstalmentFlowFactory Factory { get; }

    public IInstalmentFlow Flow { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Storage.LedgerSnapshot Snapshot => Factory.InMemoryStore!.Snapshot(Tenant);

    public void AssertInvariants() => Invariants.AssertHold(Factory, Tenant);

    public async Task<AccountId> CustomerAsync(string externalId) =>
        (await Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{externalId}", ExternalId = externalId }, Ct)).AccountId;

    /// <summary>Opens a principal-only loan whose instalments are <paramref name="amounts"/>, due daily from <paramref name="firstDue"/>.</summary>
    public async Task<ObligationId> LoanAsync(AccountId customer, string reference, DateOnly firstDue, params decimal[] amounts) =>
        (await Flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = $"open-{reference}",
            Account = customer,
            ExternalId = $"loan-{reference}",
            PaymentReference = reference,
            Components = [Component.Principal(Money.Kes(amounts.Sum()))],
            Plan = PlanSpec.Custom([.. amounts.Select((a, i) => new PlannedInstalment(firstDue.AddDays(i), [Component.Principal(Money.Kes(a))]))]),
        }, Ct)).Obligation.ObligationId;

    public async Task<ObligationId> LoanAsync(AccountId customer, string reference, IReadOnlyList<PlannedInstalment> instalments)
    {
        var components = instalments.SelectMany(i => i.Amounts).GroupBy(c => c.Key)
            .Select(g => new Component(g.Key, Money.Sum(Currency.KES, g.Select(c => c.Amount)))).ToList();
        return (await Flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = $"open-{reference}",
            Account = customer,
            ExternalId = $"loan-{reference}",
            PaymentReference = reference,
            Components = components,
            Plan = PlanSpec.Custom(instalments),
        }, Ct)).Obligation.ObligationId;
    }

    public Task<RecordPaymentResult> PayAsync(string transId, decimal amount, string billRef) =>
        Flow.Payments.RecordAsync(Paybill(transId, amount, billRef), Ct);

    public Task<ReversalResult> ReverseAsync(string transId, decimal? amount = null, string? reversalRef = null, string? key = null) =>
        Flow.Payments.ReverseAsync(new ReversePaymentRequest
        {
            IdempotencyKey = key ?? $"rev-{transId}-{amount}-{reversalRef}",
            Payment = PaymentRef.BySource(Source(transId)),
            Amount = amount is { } a ? Money.Kes(a) : null,
            ReversalReference = reversalRef is null ? null : new ExternalReference("mpesa:600123", reversalRef),
            Reason = "Provider reversal",
        }, Ct);

    public Task<ObligationView> LoanViewAsync(string reference) =>
        Flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference), cancellationToken: Ct);

    public async Task<Money> UnappliedAsync(AccountId customer) =>
        (await Flow.Queries.GetAccountAsync(customer, cancellationToken: Ct)).Unapplied.SingleOrDefault() ?? Money.Kes(0m);

    public static ExternalReference Source(string transId) => new("mpesa:600123", transId);

    public static RecordPaymentRequest Paybill(string transId, decimal amount, string billRef) =>
        Request(transId, amount, PaymentTarget.ForReference(billRef));

    public static RecordPaymentRequest Request(string transId, decimal amount, PaymentTarget target) => new()
    {
        IdempotencyKey = $"mpesa:600123:{transId}",
        Source = Source(transId),
        Amount = Money.Kes(amount),
        ValueAt = new DateTimeOffset(2026, 12, 1, 7, 0, 0, TimeSpan.Zero),
        Target = target,
    };
}
