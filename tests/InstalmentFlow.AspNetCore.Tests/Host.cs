using System.Net;
using System.Text;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Mpesa;
using InstalmentFlow.Payments.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InstalmentFlow.AspNetCore.Tests;

/// <summary>
/// A real ASP.NET Core host on a test server: an in-memory engine with tenant <c>acme-ke</c> (loan <c>LN-1</c> of
/// KES 1,000), payments with the simulator and M-Pesa, callbacks under <c>/hooks/{secret}</c> and health at <c>/health</c>.
/// Workers are off unless a test turns them on.
/// </summary>
internal sealed class Host : IAsyncDisposable
{
    public const string Tenant = "acme-ke";
    public const string Secret = "k3y-0123456789abcdef";
    public const string Paybill = "600123";

    private Host(WebApplication app, SimulatedProvider simulator)
    {
        App = app;
        Simulator = simulator;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public SimulatedProvider Simulator { get; }

    public InstalmentFlowPayments Payments => App.Services.GetRequiredService<InstalmentFlowPayments>();

    public InstalmentFlowFactory Flows => App.Services.GetRequiredService<InstalmentFlowFactory>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Host> StartAsync(
        Action<InstalmentFlowServerBuilder>? configure = null, Action<WebApplication>? map = null, string environment = "Development",
        Action<IServiceCollection>? services = null, ITenantRouter? router = null, Action<PaymentsBuilder>? payments = null)
    {
        var simulator = new SimulatedProvider("sim-secret");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddInstalmentFlow(o =>
        {
            o.UseInMemoryEngine(b => b.AddTenant(Tenant, Currency.KES, "Africa/Nairobi"));
            o.UseInMemoryPayments((_, p) =>
            {
                p.AddProvider(simulator).AddProvider(new MpesaAdapter(new HttpClient(), new NoCredentials(), new MpesaOptions()));
                if (router is not null)
                {
                    p.UseTenantRouter(router);
                }

                payments?.Invoke(p);
            });
            o.UseSimulator(simulator);
            o.Callbacks.PathSecret = Secret;
            o.Workers.Enabled = false;
            configure?.Invoke(o);
        });
        builder.Services.AddHealthChecks().AddInstalmentFlowChecks();
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapInstalmentFlowCallbacks();
        app.MapHealthChecks("/health");
        map?.Invoke(app);
        await app.StartAsync(Ct);
        var host = new Host(app, simulator);
        await host.SeedAsync();
        return host;
    }

    /// <summary>A simulator paybill callback, signed.</summary>
    public HttpRequestMessage SimulatorCallback(string transactionId, decimal amount = 300m, string reference = "LN-1", string secret = Secret, string? signature = null)
    {
        var body = Encoding.UTF8.GetBytes($$"""{"type":"payment","account":"{{Paybill}}","transactionId":"{{transactionId}}","amount":"{{amount}}","currency":"KES","time":"2026-03-02T06:00:00Z","reference":"{{reference}}"}""");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/{secret}/sim/payment") { Content = new ByteArrayContent(body) };
        request.Headers.Add(SimulatedProvider.SignatureHeader, signature ?? Simulator.Sign(body));
        return request;
    }

    public static HttpRequestMessage Post(string path, string json) =>
        new(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Sends a request from a given source address.</summary>
    public async Task<HttpContext> SendFromAsync(IPAddress source, string path, byte[] body) =>
        await App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = path;
            c.Request.Body = new MemoryStream(body);
            c.Connection.RemoteIpAddress = source;
        }, Ct);

    public async Task<Money> LoanPaidAsync() =>
        Money.Kes(1_000m).Subtract((await Flows.ForTenant(Tenant).Queries.GetObligationAsync(ObligationRef.ByReference("LN-1"), cancellationToken: Ct)).Outstanding);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        await Payments.Routing.MapAsync("sim", Paybill, Tenant, Ct);
        await Payments.Routing.MapAsync("mpesa", Paybill, Tenant, Ct);
        var flow = Flows.ForTenant(Tenant);
        var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" }, Ct);
        await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN-1",
            Components = [Component.Principal(Money.Kes(1_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, new DateOnly(2026, 3, 1)),
        }, Ct);
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<ProviderCredentials> GetAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderCredentials(new Dictionary<string, string>()));
    }
}

/// <summary>A router that takes its time, to exhaust the validation budget.</summary>
internal sealed class SlowRouter(TimeSpan delay) : ITenantRouter
{
    public async ValueTask<TenantId?> RouteAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return Host.Tenant;
    }
}
