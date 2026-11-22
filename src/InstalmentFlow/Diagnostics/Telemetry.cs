using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace InstalmentFlow;

/// <summary>
/// The names hosts subscribe to for OpenTelemetry traces and metrics, for example
/// <c>.AddSource(InstalmentFlowTelemetry.SourceName).AddMeter(InstalmentFlowTelemetry.MeterName)</c>. Every InstalmentFlow
/// package emits under these names. Attributes never carry personal data: no payers, references or amounts on spans
/// (docs/observability.md lists every span, metric and attribute).
/// </summary>
public static class InstalmentFlowTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name.</summary>
    public const string SourceName = "InstalmentFlow";

    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "InstalmentFlow";
}

/// <summary>The core's instruments (docs/observability.md).</summary>
internal static class Telemetry
{
    private static readonly string Version =
        typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static readonly ActivitySource Source = new(InstalmentFlowTelemetry.SourceName, Version);

    private static readonly Meter Meter = new(InstalmentFlowTelemetry.MeterName, Version);

    private static readonly Histogram<long> CommandDuration = Meter.CreateHistogram<long>(
        "instalmentflow.command.duration", "us", "How long a command took, from validation to commit or failure.");

    private static readonly Counter<long> ConcurrencyRetries = Meter.CreateCounter<long>(
        "instalmentflow.command.retries", "{retry}", "Command attempts repeated after another command committed first.");

    private static readonly Counter<long> PaymentsRecorded = Meter.CreateCounter<long>(
        "instalmentflow.payments.recorded", "{payment}", "Payments recorded, by outcome (allocated, held, unidentified, duplicate…).");

    private static readonly Counter<decimal> AmountReceived = Meter.CreateCounter<decimal>(
        "instalmentflow.payments.amount", "{currency_unit}", "Amounts recorded, by currency (excluding duplicates).");

    private static readonly Histogram<long> VerifyViolations = Meter.CreateHistogram<long>(
        "instalmentflow.verify.violations", "{violation}", "Invariant violations found by each Verify run; alert on anything above zero.");

    public static Activity? StartCommand(string commandType, TenantId tenant)
    {
        var activity = Source.StartActivity($"instalmentflow.command {commandType}", ActivityKind.Internal);
        activity?.SetTag("instalmentflow.command", commandType);
        activity?.SetTag("instalmentflow.tenant", tenant.Value);
        return activity;
    }

    public static void CommandFinished(Activity? activity, string commandType, long elapsedTicks, int attempts, string outcome, string? errorCode)
    {
        if (activity is not null)
        {
            activity.SetTag("instalmentflow.attempts", attempts);
            activity.SetTag("instalmentflow.outcome", outcome);
            if (errorCode is not null)
            {
                activity.SetTag("instalmentflow.error_code", errorCode);
                activity.SetStatus(ActivityStatusCode.Error, errorCode);
            }
        }

        CommandDuration.Record(elapsedTicks * 1_000_000 / Stopwatch.Frequency, new("instalmentflow.command", commandType), new("instalmentflow.outcome", outcome));
    }

    public static void Retried(string commandType) => ConcurrencyRetries.Add(1, new KeyValuePair<string, object?>("instalmentflow.command", commandType));

    public static void PaymentRecorded(RecordPaymentResult result, ExternalReference source)
    {
        var provider = source.Source.Split(':', 2)[0];
        PaymentsRecorded.Add(1, new("instalmentflow.provider", provider), new("instalmentflow.outcome", result.Outcome.ToString()));
        if (result.Outcome != RecordOutcome.Duplicate)
        {
            AmountReceived.Add(result.Amount.Amount, new KeyValuePair<string, object?>("instalmentflow.currency", result.Amount.Currency.Code));
        }

        Activity.Current?.SetTag("instalmentflow.payment_outcome", result.Outcome.ToString());
    }

    public static void Verified(int violations) => VerifyViolations.Record(violations);
}
