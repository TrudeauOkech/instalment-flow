using InstalmentFlow.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InstalmentFlow.AspNetCore;

/// <summary>
/// Runs the payments package's work on intervals: inbox processing, intent resolution, reconciliation of the
/// configured accounts, and any host work added with <see cref="WorkerOptions.Add"/>. A failed pass is logged and the
/// next one runs on schedule; work is idempotent, so several instances may run side by side.
/// </summary>
internal sealed partial class InstalmentFlowWorkers(InstalmentFlowRuntime runtime, IServiceProvider services, ILogger<InstalmentFlowWorkers> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = runtime.Settings.Workers;
        if (!settings.Enabled)
        {
            return;
        }

        await runtime.StartAsync(stoppingToken).ConfigureAwait(false);
        var loops = new List<Task>();
        if (runtime.HasPayments)
        {
            var payments = runtime.Payments;
            loops.Add(LoopAsync("inbox", settings.InboxInterval, ct => payments.Inbox.ProcessPendingAsync(ct), stoppingToken));
            loops.Add(LoopAsync("intents", settings.IntentInterval, ct => payments.IntentResolver.ResolveDueAsync(ct), stoppingToken));
            foreach (var (provider, account) in settings.ReconcileAccounts)
            {
                loops.Add(LoopAsync($"reconcile {provider}:{account}", settings.ReconciliationInterval,
                    ct => payments.Reconciliation.RunAsync(new ReconciliationRequest { Provider = provider, ProviderAccount = account }, ct), stoppingToken));
            }
        }

        foreach (var (name, interval, work) in settings.Custom)
        {
            loops.Add(LoopAsync(name, interval, ct => work(services, ct), stoppingToken));
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task LoopAsync(string name, TimeSpan interval, Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await work(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed pass must not stop the worker; the next pass retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                PassFailed(name, ex);
            }
        }
        while (await NextAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> NextAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "InstalmentFlow worker {Worker} failed; it runs again on schedule.")]
    private partial void PassFailed(string worker, Exception exception);
}

/// <summary>Health checks for InstalmentFlow (doc 03 §8).</summary>
public static class InstalmentFlowHealthChecks
{
    /// <summary>
    /// Adds <c>instalmentflow-payments</c> (inbox backlog and failures, ambiguous intents, reconciliation age of the
    /// accounts the workers reconcile) and, when <see cref="HealthOptions.MeasureOutboxLag"/> is set,
    /// <c>instalmentflow-outbox</c>.
    /// </summary>
    public static IHealthChecksBuilder AddInstalmentFlowChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddCheck<PaymentsHealthCheck>("instalmentflow-payments", tags: ["instalmentflow"]);
        builder.AddCheck<OutboxHealthCheck>("instalmentflow-outbox", tags: ["instalmentflow"]);
        return builder;
    }
}

internal sealed class PaymentsHealthCheck(InstalmentFlowRuntime runtime, TimeProvider? time = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!runtime.HasPayments)
        {
            return HealthCheckResult.Healthy("Payments are not configured.");
        }

        var options = runtime.Settings.Health;
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var health = await runtime.Payments.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, object>
        {
            ["pendingInbox"] = health.PendingInbox, ["failedInbox"] = health.FailedInbox, ["openIntents"] = health.OpenIntents, ["ambiguousIntents"] = health.AmbiguousIntents,
        };
        var unhealthy = new List<string>();
        var degraded = new List<string>();
        foreach (var (provider, account) in runtime.Settings.Workers.ReconcileAccounts)
        {
            var last = health.Reconciliations.FirstOrDefault(r => r.Provider == provider && r.ProviderAccount == account);
            if (last is null || now - last.FinishedAt > options.ReconciliationAge)
            {
                unhealthy.Add($"{provider}:{account} not reconciled for over {InstalmentFlowEndpoints.Format(options.ReconciliationAge)}");
            }
        }

        if (health.OldestPendingReceivedAt is { } oldest && now - oldest > options.InboxBacklog)
        {
            degraded.Add($"inbox backlog {InstalmentFlowEndpoints.Format(now - oldest)} old");
        }

        if (health.FailedInbox > 0)
        {
            degraded.Add($"{health.FailedInbox} inbox entries need a person");
        }

        if (health.OldestAmbiguousCreatedAt is { } ambiguous && now - ambiguous > options.AmbiguousIntentAge)
        {
            degraded.Add($"{health.AmbiguousIntents} ambiguous intents, the oldest {InstalmentFlowEndpoints.Format(now - ambiguous)} old");
        }

        var description = string.Join("; ", unhealthy.Concat(degraded));
        return unhealthy.Count > 0 ? HealthCheckResult.Unhealthy(description, data: data)
            : degraded.Count > 0 ? HealthCheckResult.Degraded(description, data: data)
            : HealthCheckResult.Healthy("Payments are flowing.", data);
    }
}

internal sealed class OutboxHealthCheck(InstalmentFlowRuntime runtime, IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var options = runtime.Settings.Health;
        if (options.MeasureOutboxLag is not { } measure)
        {
            return HealthCheckResult.Healthy("The outbox is not monitored.");
        }

        var lag = await measure(services, cancellationToken).ConfigureAwait(false);
        return lag is { } l && l > options.OutboxLag
            ? HealthCheckResult.Degraded($"The oldest undelivered event is {InstalmentFlowEndpoints.Format(l)} old.")
            : HealthCheckResult.Healthy(lag is null ? "Nothing waiting." : $"Oldest undelivered event: {InstalmentFlowEndpoints.Format(lag.Value)}.");
    }
}
