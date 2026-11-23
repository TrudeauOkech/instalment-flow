using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace InstalmentFlow.AspNetCore.Tests;

/// <summary>Tenant context, ProblemDetails, health checks, simulator endpoints and workers.</summary>
public sealed class HostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void LoanEndpoint(WebApplication app) =>
        app.MapGet("/tenants/{tenant}/loans/{reference}", async (string reference, IInstalmentFlow flow) =>
            (await flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference))).Outstanding.Amount);

    [Fact]
    public async Task A_request_gets_the_engine_of_its_routes_tenant()
    {
        await using var host = await Host.StartAsync(map: LoanEndpoint, services: s => s.UseTenantContext<RouteTenantContext>());

        var outstanding = await host.Client.GetFromJsonAsync<decimal>($"/tenants/{Host.Tenant}/loans/LN-1", Ct);
        var otherTenant = await host.Client.GetAsync("/tenants/nobody/loans/LN-1", Ct);

        Assert.Equal(1_000m, outstanding);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        Assert.Equal("IF5002", await ErrorCodeAsync(otherTenant));
    }

    [Fact]
    public async Task A_request_gets_the_engine_of_its_users_tenant_claim_and_is_refused_without_one()
    {
        await using var host = await Host.StartAsync(map: app =>
        {
            app.Use((context, next) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Tenant", out var tenant))
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", tenant.ToString())], "test"));
                }

                return next(context);
            });
            app.MapGet("/loans/{reference}", async (string reference, IInstalmentFlow flow) =>
                (await flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference))).Outstanding.Amount);
        });

        using var withClaim = new HttpRequestMessage(HttpMethod.Get, "/loans/LN-1");
        withClaim.Headers.Add("X-Test-Tenant", Host.Tenant);
        var allowed = await host.Client.SendAsync(withClaim, Ct);
        var anonymous = await host.Client.GetAsync("/loans/LN-1", Ct);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Fact]
    public async Task Engine_errors_become_problem_details_with_their_error_code()
    {
        await using var host = await Host.StartAsync(services: s => s.UseTenantContext<RouteTenantContext>(), map: app =>
        {
            LoanEndpoint(app);
            app.MapPost("/tenants/{tenant}/payments", async (IInstalmentFlow flow, decimal amount) =>
                await flow.Payments.RecordAsync(new RecordPaymentRequest
                {
                    IdempotencyKey = "same-key", Source = new ExternalReference("bank:1", $"T{amount}"), Amount = Money.Kes(amount),
                    ValueAt = DateTimeOffset.UnixEpoch, Target = PaymentTarget.ForReference("LN-1"),
                }));
        });

        var missing = await host.Client.GetAsync($"/tenants/{Host.Tenant}/loans/NOPE", Ct);
        var invalid = await host.Client.PostAsync($"/tenants/{Host.Tenant}/payments?amount=-5", null, Ct);
        await host.Client.PostAsync($"/tenants/{Host.Tenant}/payments?amount=5", null, Ct);
        var reused = await host.Client.PostAsync($"/tenants/{Host.Tenant}/payments?amount=6", null, Ct);

        Assert.Equal((HttpStatusCode.NotFound, "IF1010"), (missing.StatusCode, await ErrorCodeAsync(missing)));
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.IdempotencyKeyReused), (reused.StatusCode, await ErrorCodeAsync(reused)));
    }

    [Fact]
    public async Task Health_reports_failed_inbox_entries_missing_reconciliation_and_outbox_lag()
    {
        await using var healthy = await Host.StartAsync();
        Assert.Equal("Healthy", await (await healthy.Client.GetAsync("/health", Ct)).Content.ReadAsStringAsync(Ct));

        await using var lagging = await Host.StartAsync(
            o => o.Health.MeasureOutboxLag = (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(30)), payments: p => p.Inbox.MaxAttempts = 1);
        await lagging.Payments.Routing.RemoveAsync("sim", Host.Paybill, Ct);
        await lagging.Client.SendAsync(lagging.SimulatorCallback("SIMH9"), Ct);
        await lagging.Payments.Inbox.ProcessPendingAsync(Ct);   // unroutable, one attempt: failed
        Assert.Equal("Degraded", await (await lagging.Client.GetAsync("/health", Ct)).Content.ReadAsStringAsync(Ct));

        await using var stale = await Host.StartAsync(o => o.Workers.ReconcileAccounts.Add(("sim", Host.Paybill)));
        var response = await stale.Client.GetAsync("/health", Ct);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), (response.StatusCode, await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task The_simulator_endpoints_drive_a_payment_through_the_whole_flow_in_development()
    {
        await using var host = await Host.StartAsync(map: app => app.MapInstalmentFlowSimulator());

        var paid = await host.Client.PostAsJsonAsync("/simulator/pay", new { account = Host.Paybill, amount = 250m, reference = "LN-1", faults = "Duplicate" }, Ct);
        var delivered = await host.Client.PostAsync("/simulator/deliver", null, Ct);
        await host.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal(2, (await delivered.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("delivered").GetInt32());
        Assert.Equal(Money.Kes(250m), await host.LoanPaidAsync());
    }

    [Fact]
    public async Task The_simulator_is_refused_outside_development_unless_allowed()
    {
        var production = await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(map: app => app.MapInstalmentFlowSimulator(), environment: "Production"));
        var staging = await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(map: app => app.MapInstalmentFlowSimulator(), environment: "Staging"));
        await using var allowed = await Host.StartAsync(o => o.UseSimulator(new Payments.Simulation.SimulatedProvider("x"), allowOutsideDevelopment: true), map: app => app.MapInstalmentFlowSimulator(), environment: "Production");

        Assert.Contains("Production", production.Message, StringComparison.Ordinal);
        Assert.Contains("Staging", staging.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await allowed.Client.GetAsync("/simulator/transactions", Ct)).StatusCode);
    }

    [Fact]
    public async Task Workers_process_the_inbox_on_their_own()
    {
        await using var host = await Host.StartAsync(o =>
        {
            o.Workers.Enabled = true;
            o.Workers.InboxInterval = TimeSpan.FromMilliseconds(50);
            o.Workers.IntentInterval = TimeSpan.FromMilliseconds(50);
        });

        await host.Client.SendAsync(host.SimulatorCallback("SIMW1"), Ct);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await host.LoanPaidAsync() != Money.Kes(300m) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(Money.Kes(300m), await host.LoanPaidAsync());
    }

    [Fact]
    public async Task Several_inbox_workers_record_each_payment_once_even_when_callbacks_are_repeated()
    {
        await using var host = await Host.StartAsync(o =>
        {
            o.Workers.Enabled = true;
            o.Workers.InboxWorkers = 4;
            o.Workers.InboxInterval = TimeSpan.FromMilliseconds(20);
        });

        var callbacks = Enumerable.Range(0, 40).SelectMany(i => new[] { $"SIMP{i}", $"SIMP{i}" })   // each delivered twice
            .Select(id => host.Client.SendAsync(host.SimulatorCallback(id, amount: 10m), Ct));
        await Task.WhenAll(callbacks);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (await host.LoanPaidAsync() != Money.Kes(400m) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        await Task.Delay(300, Ct);   // any double recording would land by now
        Assert.Equal(Money.Kes(400m), await host.LoanPaidAsync());
        Assert.True((await host.Flows.ForTenant(Host.Tenant).Queries.VerifyAsync(VerifyScope.Tenant, Ct)).Passed);
    }

    [Fact]
    public void Inbox_workers_default_to_eight_and_must_be_at_least_one()
    {
        var options = new WorkerOptions();

        Assert.Equal(8, options.InboxWorkers);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.InboxWorkers = 0);
    }

    [Fact]
    public async Task A_custom_worker_runs_on_its_interval_and_survives_its_failures()
    {
        var runs = 0;
        await using var host = await Host.StartAsync(o =>
        {
            o.Workers.Enabled = true;
            o.Workers.Add("flaky", TimeSpan.FromMilliseconds(20), (_, _) => Interlocked.Increment(ref runs) % 2 == 0 ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref runs) < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(Volatile.Read(ref runs) >= 5);
    }

    [Fact]
    public async Task Outside_development_a_provider_without_authenticated_callbacks_or_allowed_networks_is_warned_about()
    {
        var warnings = new Captured();
        await using (await Host.StartAsync(environment: "Production", services: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(warnings)))
        {
        }

        var unprotected = new Captured();
        await using (await Host.StartAsync(o => o.Callbacks.AllowedNetworks["mpesa"] = [System.Net.IPNetwork.Parse("196.201.214.0/24")], environment: "Production",
            services: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(unprotected)))
        {
        }

        Assert.Contains(warnings.Messages, m => m.Contains("provider mpesa", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings.Messages, m => m.Contains("provider sim", StringComparison.Ordinal));   // signed callbacks
        Assert.DoesNotContain(unprotected.Messages, m => m.Contains("provider mpesa", StringComparison.Ordinal));
    }

    private sealed class Captured : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("errorCode", out var code) ? code.GetString() : null;
}
