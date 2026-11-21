// Sample 05: InstalmentFlow in an ASP.NET Core host. It wires up:
// - the engine and payments, created at start-up (in memory here; PostgreSQL through UseEngine and UsePayments);
// - callback endpoints with a path secret, simulator endpoints and the host's own API;
// - background workers, ProblemDetails and health checks.
// By default it runs against itself: it starts on a free local port, pays a loan through the simulator's HTTP
// endpoints (with a duplicate callback injected), lets the workers record the payment, and checks the result.
// Run it with --serve to keep it running and try the endpoints yourself.
using System.Net.Http.Json;
using System.Text.Json;
using InstalmentFlow;
using InstalmentFlow.AspNetCore;
using InstalmentFlow.Payments.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var serve = args.Contains("--serve");
var simulator = new SimulatedProvider("simulator-callback-secret");
// The simulator is refused in Production (ADR-016), which is ASP.NET Core's default environment; this sample is for development.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args, EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Development,
});
builder.WebHost.UseUrls(serve ? "http://127.0.0.1:5080" : "http://127.0.0.1:0");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddInstalmentFlow(o =>
{
    o.UseInMemoryEngine(b => b.AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"));
    o.UseInMemoryPayments((_, p) => p.AddProvider(simulator));
    o.UseSimulator(simulator);
    o.Callbacks.PathSecret = "change-me-to-a-long-random-secret";
    o.Workers.InboxInterval = TimeSpan.FromMilliseconds(200);
});
builder.Services.UseTenantContext<RouteTenantContext>();   // the host's API uses /tenants/{tenant}/…; authorise callers in a real host
builder.Services.AddHealthChecks().AddInstalmentFlowChecks();

var app = builder.Build();
app.UseExceptionHandler();
app.MapInstalmentFlowCallbacks();          // /hooks/{secret}/{provider}/{kind}
app.MapInstalmentFlowSimulator();          // /simulator/… (refused in Production)
app.MapHealthChecks("/health");
app.MapGet("/tenants/{tenant}/loans/{reference}", async (string reference, IInstalmentFlow flow) =>
{
    var loan = await flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference));
    return new { loan.PaymentReference, outstanding = loan.Outstanding.ToString(), loan.PaidThroughDate };
});

await app.StartAsync();
var payments = app.Services.GetRequiredService<InstalmentFlow.Payments.InstalmentFlowPayments>();
await payments.Routing.MapAsync("sim", "600123", "acme-ke");
var flow = app.Services.GetRequiredService<InstalmentFlowFactory>().ForTenant("acme-ke");
var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-C-1", ExternalId = "C-1" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan-L-1", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN-1",
    Components = [Component.Principal(Money.Kes(1_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, DateOnly.FromDateTime(DateTime.UtcNow)),
});

if (serve)
{
    Console.WriteLine("""
        Serving on http://127.0.0.1:5080. Try:
          curl -X POST localhost:5080/simulator/pay -H 'content-type: application/json' -d '{"account":"600123","amount":300,"reference":"LN-1"}'
          curl -X POST localhost:5080/simulator/deliver
          curl localhost:5080/tenants/acme-ke/loans/LN-1
          curl localhost:5080/health
        """);
    await app.WaitForShutdownAsync();
    return 0;
}

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
using var http = new HttpClient { BaseAddress = new Uri(address) };
await http.PostAsJsonAsync("/simulator/pay", new { account = "600123", amount = 300m, reference = "LN-1", faults = "Duplicate" });
var delivered = await http.PostAsync("/simulator/deliver", null);
Console.WriteLine($"Simulator delivered: {await delivered.Content.ReadAsStringAsync()}");

JsonElement loan = default;
for (var i = 0; i < 50; i++)   // the inbox worker records the payment within a few hundred milliseconds
{
    loan = await http.GetFromJsonAsync<JsonElement>("/tenants/acme-ke/loans/LN-1");
    if (loan.GetProperty("outstanding").GetString() == "KES 700.00")
    {
        break;
    }

    await Task.Delay(100);
}

var health = await http.GetStringAsync("/health");
var missing = await http.GetAsync("/tenants/acme-ke/loans/NOPE");
Console.WriteLine($"Loan LN-1: {loan}");
Console.WriteLine($"Health: {health}; unknown loan: {(int)missing.StatusCode} {(await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode")}");

var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant);
await app.StopAsync();
var ok = loan.GetProperty("outstanding").GetString() == "KES 700.00" && health == "Healthy" && (int)missing.StatusCode == 404 && report.Passed;
Console.WriteLine(ok ? "OK: paid once despite the duplicate callback; the ledger verifies." : "FAILED");
return ok ? 0 : 1;
