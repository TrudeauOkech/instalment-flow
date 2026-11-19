# Document 4 — API and SDK Design

Design goals:
1. **Five-minute first success.** The quick start runs in-memory with no configuration beyond one tenant.
2. **A grouped, tenant-scoped facade.** `flow.Payments`, `flow.Charges`, `flow.Exceptions`… are easy to discover, and there is no tenant-less entry point (ADR-007).
3. **Requests are records with `required` init properties** (readable at the call site, enforced by the compiler). Results are typed, and expected business outcomes (duplicate, held, unidentified) are values, not exceptions.
4. **Every money-moving request requires an `IdempotencyKey`.** It is `required` on the base request type, so it cannot be forgotten.
5. **Wrong code is hard to write.** `Money` cannot be built from `double`, currencies cannot be mixed, and dates are `DateOnly` or `DateTimeOffset`, never `DateTime`.

## 1. Installation

```bash
dotnet add package InstalmentFlow                          # core + in-memory store
dotnet add package InstalmentFlow.EntityFrameworkCore      # PostgreSQL persistence, RLS, outbox
dotnet add package InstalmentFlow.Payments                 # provider SPI, inbox, intents, simulator
dotnet add package InstalmentFlow.Payments.Mpesa
dotnet add package InstalmentFlow.AspNetCore               # callback endpoints, tenant context
dotnet add package InstalmentFlow.Testing                  # test helpers (test projects only)
```

## 2. Quick start (console, in-memory)

```csharp
using InstalmentFlow;

var factory = InstalmentFlowFactory.CreateInMemory(t => t
    .AddTenant("acme-ke", Currency.KES, "Africa/Nairobi", policy => policy with
    {
        PaymentReference = new ReferenceFormat(@"^LN-\d{6}$"),
    }));

IInstalmentFlow flow = factory.ForTenant("acme-ke");

var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest
{
    IdempotencyKey = "acct-C881",
    ExternalId = "C-881",
});

await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "open-LN-100001",
    Account = customer.AccountId,
    ExternalId = "loan-2041",                // the host LMS's id
    PaymentReference = "LN-100001",          // what the customer types at the paybill
    Components = [Component.Principal(Money.Kes(15_000))],
    Plan = PlanSpec.Equal(
        Frequency.Daily, count: 270, firstDue: new DateOnly(2026, 12, 1), grace: GracePeriod.OfDays(2),
        deposit: new Deposit(Money.Kes(1_500), DueDate: new DateOnly(2026, 11, 18))),   // 13,500 / 270 = 50 per day
});

foreach (var (i, amount) in new[] { 100m, 150m, 50m, 200m }.Index())
{
    var r = await flow.Payments.RecordAsync(new RecordPaymentRequest
    {
        IdempotencyKey = $"manual:R{i}",
        Source = new ExternalReference("manual", $"R{i}"),
        Amount = Money.Kes(amount),
        ValueAt = DateTimeOffset.UtcNow,
        Target = PaymentTarget.ForReference("LN-100001"),   // or .ForObligation(id), .ForCustomer(id), .Unknown
    });
    Console.WriteLine($"{r.Outcome}: allocated {r.Allocated}, unapplied {r.UnappliedAfter}");
}

var loan = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN-100001"));
Console.WriteLine($"Outstanding {loan.Outstanding}, paid through {loan.PaidThroughDate}");
```

## 3. ASP.NET Core with PostgreSQL, multiple tenants, and M-Pesa

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInstalmentFlow()
    .UseEntityFrameworkCore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("Ledger")))
    .UseTenantContext<ClaimsTenantContext>()                 // API requests: tenant from the caller's identity
    .AddPayments(p => p
        .UseProviderCredentials<KeyVaultCredentialSource>()  // per-tenant secrets come from the host's store
        .AddMpesa()                                          // provider account → tenant routing is in tenant config
        .AddReconciliation(r => r.Every = TimeSpan.FromHours(1))
        .AddSimulator(builder.Environment.IsDevelopment()))  // explicit; refused in Production by default
    .AddOutboxPublisher<MyEventPublisher>();

var app = builder.Build();
await app.Services.MigrateInstalmentFlowAsync();             // explicit, never silent
app.MapInstalmentFlowCallbacks("/hooks/{secret}")             // tenant resolved from shortcode → tenant mapping
   .RequireMpesaSourceIps();
app.MapPost("/loans/{reference}/charges", PostLateFee);       // the host's own API
app.Run();

static async Task<IResult> PostLateFee(string reference, LateFeeDto dto, IInstalmentFlow flow)  // scoped to the request's tenant
{
    var result = await flow.Charges.PostAsync(new PostChargeRequest
    {
        IdempotencyKey = $"late-fee:{reference}:{dto.ForDueDate:yyyyMMdd}",
        Obligation = ObligationRef.ByReference(reference),
        Type = ChargeType.Penalty,
        Amount = Money.Of(dto.Amount, "KES"),
        Description = "Late payment charge",
        ReasonCode = "LATE_PAYMENT",
        DuePolicy = ChargeDue.NextUnpaidInstalment,          // or .Immediately, .OnInstalment(seq)
    });
    return Results.Ok(result);
}
```

Tenant administration is the only API that is not tenant-scoped:

```csharp
await tenantAdmin.CreateAsync(new CreateTenantRequest
{
    TenantId = "acme-ke", BaseCurrency = Currency.KES, TimeZone = "Africa/Nairobi",
    Policy = TenantPolicy.Default with
    {
        Allocation = AllocationStrategies.OldestDueFirst.V1,
        ComponentOrder = [ComponentType.Penalty, ComponentType.Fee, ComponentType.Interest, ComponentType.Principal],
        FutureInstalments = FutureInstalmentPolicy.ApplyToNextInstalments,
        SoleObligation = SoleObligationPolicy.AutoAllocate,
        MultipleObligations = MultipleObligationsPolicy.Hold,       // default; ADR-009
        PaymentReference = new ReferenceFormat(@"^LN-\d{6}$", ReferenceNormaliser.UpperTrimStripSpaces),
    },
    ProviderAccounts = [new ProviderAccount("mpesa", "600123")],    // unique across tenants
});
await tenantAdmin.UpdatePolicyAsync("acme-ke", p => p with { DefaultGrace = GracePeriod.OfDays(3) }); // creates policy v2
```

## 4. Public surface

```csharp
namespace InstalmentFlow;

public interface IInstalmentFlowFactory { ITenantAdmin Tenants { get; } IInstalmentFlow ForTenant(TenantId tenant); }

public interface IInstalmentFlow                 // always tenant-scoped
{
    TenantId Tenant { get; }
    IAccounts           Accounts    { get; }
    IObligations        Obligations { get; }
    IPlans              Plans       { get; }
    IPayments           Payments    { get; }
    IAllocations        Allocations { get; }
    ICharges            Charges     { get; }
    IRefunds            Refunds     { get; }
    IPaymentExceptions  Exceptions  { get; }
    IInstalmentQueries  Queries     { get; }
}

public interface IObligations
{
    Task<ObligationOpened>   OpenAsync(OpenObligationRequest r, CancellationToken ct = default);
    Task<ObligationCancelled> CancelAsync(CancelObligationRequest r, CancellationToken ct = default);  // explicit write-off lines + reason
    Task<SettlementQuote>    QuoteEarlySettlementAsync(QuoteSettlementRequest r, CancellationToken ct = default);
}
public interface IPlans
{
    Task<PlanSuperseded> SupersedeAsync(SupersedePlanRequest r, CancellationToken ct = default);  // only without financial history
}
public interface IPayments
{
    Task<RecordPaymentResult> RecordAsync(RecordPaymentRequest r, CancellationToken ct = default);   // always records (ADR-002)
    Task<ReversalResult>      ReverseAsync(ReversePaymentRequest r, CancellationToken ct = default); // full or partial
}
public interface IAllocations
{
    Task<AllocationResult> AllocateAsync(AllocateFundsRequest r, CancellationToken ct = default);    // manual / customer-directed
}
public interface ICharges
{
    Task<ChargeResult> PostAsync(PostChargeRequest r, CancellationToken ct = default);   // interest, fee, penalty from the host
    Task<WaiverResult> WaiveAsync(WaiveRequest r, CancellationToken ct = default);
    Task<ChargeResult> ReverseAsync(ReverseChargeRequest r, CancellationToken ct = default);
}
public interface IRefunds                                    // recorded only (ADR-010)
{
    Task<RefundResult> RequestAsync(RequestRefundRequest r, CancellationToken ct = default);
    Task<RefundResult> CompleteAsync(CompleteRefundRequest r, CancellationToken ct = default);   // Confirmed | Failed
}
public interface IPaymentExceptions
{
    IAsyncEnumerable<PaymentExceptionView> ListOpenAsync(ExceptionFilter? filter = null, CancellationToken ct = default);
    Task<ResolutionResult> AssignToCustomerAsync(AssignToCustomerRequest r, CancellationToken ct = default);
    Task<ResolutionResult> AllocateToObligationAsync(AllocateToObligationRequest r, CancellationToken ct = default);
    Task<ResolutionResult> MarkForRefundAsync(MarkForRefundRequest r, CancellationToken ct = default);
    Task<ResolutionResult> DismissAsync(DismissExceptionRequest r, CancellationToken ct = default);
}
public interface IInstalmentQueries
{
    Task<AccountSummary>      GetAccountAsync(AccountRef a, AsOf? asOf = null, CancellationToken ct = default);
    Task<ObligationView>      GetObligationAsync(ObligationRef o, AsOf? asOf = null, CancellationToken ct = default);
    Task<ScheduleView>        GetScheduleAsync(ObligationRef o, AsOf? asOf = null, CancellationToken ct = default); // incl. superseded versions
    Task<PaymentTrace>        TracePaymentAsync(PaymentRef p, CancellationToken ct = default);   // receipt → lot → allocations → reversals
    IAsyncEnumerable<StatementLine>      GetStatementAsync(AccountRef a, DateOnly from, DateOnly to, CancellationToken ct = default);
    IAsyncEnumerable<JournalTransaction> ReadJournalAsync(JournalCursor from, CancellationToken ct = default);     // GL export
    Task<VerificationReport>  VerifyAsync(VerifyScope scope, CancellationToken ct = default);
}
```

### Requests and results (selected)

```csharp
public abstract record InstalmentFlowRequest
{
    public required string IdempotencyKey { get; init; }     // ≤ 128 chars, unique per tenant
}

public sealed record RecordPaymentRequest : InstalmentFlowRequest
{
    public required ExternalReference Source { get; init; }  // ("mpesa:600123", "TJ3K9XYZ")
    public required Money Amount { get; init; }
    public required DateTimeOffset ValueAt { get; init; }
    public required PaymentTarget Target { get; init; }
    public AllocationMode Allocation { get; init; } = AllocationMode.PerTenantPolicy;   // or HoldAsUnapplied
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public abstract record PaymentTarget
{
    public static PaymentTarget Obligation(ObligationRef o) => new ObligationTarget(o);  // STK push: host knows the loan
    public static PaymentTarget Reference(string raw) => new ReferenceTarget(raw);       // paybill BillRefNumber
    public static PaymentTarget Customer(AccountRef a) => new CustomerTarget(a);         // customer known, loan not
    public static readonly PaymentTarget Unknown = new UnknownTarget();
}

public sealed record RecordPaymentResult(
    RecordOutcome Outcome,       // Allocated | PartiallyAllocated | HeldUnapplied | Unidentified | Duplicate
    PaymentId PaymentId,
    AccountId? AccountId,
    Money Allocated,
    Money UnappliedAfter,
    PaymentExceptionView? Exception,         // set when identification raised one (ADR-009)
    IReadOnlyList<AllocationLineView> Lines,
    IReadOnlyList<InstalmentRef> InstalmentsSettled,
    IReadOnlyList<ObligationId> ObligationsSettled,
    JournalTxId JournalTx);

public sealed record PostChargeRequest : InstalmentFlowRequest
{
    public required ObligationRef Obligation { get; init; }
    public required ChargeType Type { get; init; }           // Interest | Fee | Penalty
    public required Money Amount { get; init; }
    public required string Description { get; init; }
    public string? ReasonCode { get; init; }
    public string? ChargeCode { get; init; }                  // e.g. "insurance"; later keys distribution (ADR-012)
    public ChargeDue DuePolicy { get; init; } = ChargeDue.PerTenantPolicy;
}
```

`ObligationRef`, `AccountRef` and `PaymentRef` accept the internal ID, the host's external ID, or (for obligations) the payment reference. All of them are resolved within the tenant only.

**Error model:**
- *Expected outcomes* are values: `Duplicate`, `HeldUnapplied`, `Unidentified`, exceptions raised, and `ReversalResult.Deficit`.
- *Rule violations* throw `InstalmentFlowException` subclasses, each with a stable `ErrorCode`:
  - `IF1001` CurrencyMismatch;
  - `IF1002` IdempotencyKeyReused (same key, different payload);
  - `IF1011` PaymentConflict (same provider reference, different amount);
  - `IF1012` ExceptionNotResolvable;
  - `IF2001` ReversalExceedsPayment;
  - `IF3001` ConcurrencyRetriesExhausted;
  - `IF4001` PlanHasFinancialHistory;
  - `IF5001` TenantMismatch.

  `InstalmentFlow.AspNetCore` maps them to `ProblemDetails`.

## 5. Extension points

```csharp
[AllocationStrategy("acme.insurance-first", version: 1)]
public sealed class InsuranceFirst : IAllocationStrategy       // pure; the engine validates its plan (ADR-006)
{
    public StrategyRef Ref => new("acme.insurance-first", 1);
    public AllocationPlan Plan(AllocationContext ctx) =>        // ctx contains only eligible obligations (ADR-009)
        AllocationPlan.Build(ctx)
            .Take(t => t.Component == ComponentType.Fee && t.ChargeCode == "insurance", order: Targets.ByDueDate)
            .ThenApply(AllocationStrategies.OldestDueFirst.V1);
}
```

| Interface | Purpose |
|---|---|
| `IAllocationStrategy` | Custom allocation rules |
| `IScheduleGenerator` | Custom plan shapes (seasonal plans for farmers, balloon payments) |
| `IBusinessCalendar` | Holidays and non-business-day shifting |
| `IPaymentReferenceResolver` | Extra reference matching (legacy formats, customer-level references). It cannot bypass the ambiguity rule. |
| `ITenantContext`, `ITenantRouter` | Resolve the tenant for a request, or for a provider account shared between tenants |
| `IProviderCredentialSource` | Per-tenant provider secrets from the host |
| `IIntegrationEventPublisher` | Outbox delivery |
| `IPaymentProviderAdapter` and capability interfaces | New providers |
| `IStatementSource` | Reconciliation inputs |
| `IAccountStore`, `IIdempotencyStore`, `ITenantPolicyStore` | Other databases |
| `IPayloadProtector` | Encryption of raw provider payloads |

**Post-1.0, designed but not shipped:** `IChargePolicy` (automatic penalties), `IInterestCalculator`, `IRefundInitiator` and `IReversalInitiator` (ADR-010), and `IDistributionRule` (ADR-012).

## 6. Testing helpers (`InstalmentFlow.Testing`)

```csharp
[Fact]
public async Task Ambiguous_reference_is_held_not_allocated()
{
    var s = await Scenario.InMemory(tenant: "t1")
        .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Daily(Money.Kes(500), count: 1)))
        .Given(a => a.Customer("C2").Loan("LN-100002", Plan.Daily(Money.Kes(500), count: 1)))
        .Given(a => a.Resolver(r => r.Map("LN-10000", "LN-100001", "LN-100002")))   // fuzzy legacy reference
        .When(a => a.PaybillPayment(amount: 500, billRef: "LN-10000", transId: "TX1"))
        .Then(a => a.Payment("TX1").Outcome.ShouldBe(RecordOutcome.Unidentified))
        .Then(a => a.OpenException("TX1").Reason.ShouldBe(ExceptionReason.AmbiguousReference))
        .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(500)))
        .RunAsync();

    s.ShouldSatisfyAllInvariants();            // VerifyAsync, including I-19 and I-20
}

// Simulator-driven fault scenarios
var sim = scenario.Simulator.Mpesa(shortCode: "600123");
await sim.Paybill(150, billRef: "LN-100001").DeliverTimes(5).WithJitter(TimeSpan.FromSeconds(2));
await sim.StkPush(500).TimeOutThenSucceed(after: TimeSpan.FromMinutes(3));
```

## 7. API stability rules

- `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` are kept per package. Breaking changes need a major version.
- Strategy behaviour is versioned (`OldestDueFirst.V1`, `.V2`…). Tenants pin a version, so they never change behaviour silently.
- Persisted enums use explicit string codes.
- Interfaces that are designed but not shipped (see §5) do not appear in the 1.0 public API.
