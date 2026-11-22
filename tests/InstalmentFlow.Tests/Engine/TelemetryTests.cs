using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InstalmentFlow.Tests;

/// <summary>OpenTelemetry instruments (docs/observability.md): spans and metrics under one name, without personal data.</summary>
public sealed class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recorded_payment_emits_a_command_span_and_metrics_without_personal_data()
    {
        var tenant = $"tel-{Guid.NewGuid():N}"[..20];
        var spans = new ConcurrentBag<Activity>();
        var measurements = new ConcurrentBag<(string Name, object Value, Dictionary<string, object?> Tags)>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name == InstalmentFlowTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (Equals(a.GetTagItem("instalmentflow.tenant"), tenant)) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(activities);
        using var meters = new MeterListener { InstrumentPublished = (i, l) => { if (i.Meter.Name == InstalmentFlowTelemetry.MeterName) { l.EnableMeasurementEvents(i); } } };
        void Capture<T>(Instrument i, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            if ((copy.TryGetValue("instalmentflow.provider", out var p) && Equals(p, "teltest")) || i.Name is "instalmentflow.command.duration" or "instalmentflow.payments.amount")
            {
                measurements.Add((i.Name, value!, copy));
            }
        }

        meters.SetMeasurementEventCallback<long>(Capture);
        meters.SetMeasurementEventCallback<decimal>(Capture);
        meters.Start();

        var flow = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant(tenant, Currency.KES, "Africa/Nairobi")).ForTenant(tenant);
        var request = new RecordPaymentRequest
        {
            IdempotencyKey = "p1", Source = new ExternalReference("teltest:600123", "SECRETRECEIPT1"), Amount = Money.Kes(150m), ValueAt = DateTimeOffset.UnixEpoch,
            Target = PaymentTarget.ForReference("PRIVATE-REF-77"), Metadata = new Dictionary<string, string> { ["payer"] = "254700000001" },
        };
        await flow.Payments.RecordAsync(request, Ct);
        await flow.Payments.RecordAsync(request, Ct);   // a replay
        await Assert.ThrowsAsync<RuleViolationException>(() => flow.Payments.RecordAsync(request with { Amount = Money.Kes(151m) }, Ct));   // key reused
        meters.RecordObservableInstruments();

        var commands = spans.Where(s => s.DisplayName == "instalmentflow.command RecordPayment").ToList();
        Assert.Equal(3, commands.Count);
        Assert.Equal(["committed", "failed", "replayed"], commands.Select(s => (string)s.GetTagItem("instalmentflow.outcome")!).Order());
        var failed = commands.Single(s => Equals(s.GetTagItem("instalmentflow.outcome"), "failed"));
        Assert.Equal((ActivityStatusCode.Error, ErrorCodes.IdempotencyKeyReused), (failed.Status, failed.GetTagItem("instalmentflow.error_code")));

        var recorded = measurements.Where(m => m.Name == "instalmentflow.payments.recorded").ToList();
        Assert.Equal(["Duplicate", "Unidentified"], recorded.Select(m => (string)m.Tags["instalmentflow.outcome"]!).Order());
        Assert.Contains(measurements, m => m.Name == "instalmentflow.payments.amount" && (decimal)m.Value == 150m && Equals(m.Tags["instalmentflow.currency"], "KES"));
        Assert.Contains(measurements, m => m.Name == "instalmentflow.command.duration");

        var everything = spans.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString() ?? string.Empty)
            .Concat(measurements.SelectMany(m => m.Tags.Values).Select(v => v?.ToString() ?? string.Empty));
        Assert.DoesNotContain(everything, v => v.Contains("SECRETRECEIPT1", StringComparison.Ordinal) || v.Contains("PRIVATE-REF-77", StringComparison.Ordinal)
            || v.Contains("254700000001", StringComparison.Ordinal) || v.Contains("600123", StringComparison.Ordinal));
    }
}
