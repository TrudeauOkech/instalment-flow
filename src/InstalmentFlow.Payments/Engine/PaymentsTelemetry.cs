using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace InstalmentFlow.Payments.Engine;

/// <summary>The payments package's instruments, under the same names as the core's (docs/observability.md).</summary>
internal static class PaymentsTelemetry
{
    private static readonly string Version =
        typeof(PaymentsTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static readonly ActivitySource Source = new(InstalmentFlowTelemetry.SourceName, Version);

    private static readonly Meter Meter = new(InstalmentFlowTelemetry.MeterName, Version);

    private static readonly Counter<long> Received = Meter.CreateCounter<long>(
        "instalmentflow.inbox.received", "{event}", "Provider events received, by provider and outcome (stored, duplicate, held, rejected).");

    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(
        "instalmentflow.inbox.processed", "{event}", "Inbox entries processed, by provider and status (processed, retrying, failed).");

    private static readonly Counter<long> Intents = Meter.CreateCounter<long>(
        "instalmentflow.intents", "{intent}", "Intent state changes, by provider and new status (submitted, ambiguous, confirmed, failed, expired).");

    private static readonly Counter<long> StatusQueries = Meter.CreateCounter<long>(
        "instalmentflow.provider.status_queries", "{query}", "Provider status queries, by provider and answer.");

    private static readonly Counter<long> ReconciledLines = Meter.CreateCounter<long>(
        "instalmentflow.reconciliation.lines", "{line}", "Statement lines reconciled, by provider and classification.");

    private static readonly Counter<long> Discrepancies = Meter.CreateCounter<long>(
        "instalmentflow.reconciliation.discrepancies", "{discrepancy}", "Discrepancies raised, by provider and kind.");

    public static Activity? Start(string name, string provider)
    {
        var activity = Source.StartActivity($"instalmentflow.{name}", ActivityKind.Internal);
        activity?.SetTag("instalmentflow.provider", provider);
        return activity;
    }

    public static void EventReceived(string provider, string outcome) => Received.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.outcome", outcome));

    public static void EntryProcessed(Activity? activity, string provider, InboxStatus status, int attempts)
    {
        activity?.SetTag("instalmentflow.status", status.ToString());
        activity?.SetTag("instalmentflow.attempts", attempts);
        if (status != InboxStatus.Processed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, status.ToString());
        }

        Processed.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.status", status == InboxStatus.Pending ? "Retrying" : status.ToString()));
    }

    public static void IntentChanged(string provider, PaymentIntentStatus status) =>
        Intents.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.status", status.ToString()));

    public static void StatusQueried(string provider, string answer) =>
        StatusQueries.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.status", answer));

    public static void Reconciled(string provider, ReconciliationClassification classification) =>
        ReconciledLines.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.classification", classification.ToString()));

    public static void DiscrepancyRaised(string provider, DiscrepancyKind kind) =>
        Discrepancies.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.kind", kind.ToString()));
}
