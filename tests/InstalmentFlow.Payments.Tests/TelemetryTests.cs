using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>The payments package emits under the core's telemetry names (docs/observability.md).</summary>
public sealed class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Received_and_processed_events_are_counted_and_traced_by_provider()
    {
        var provider = $"t{Guid.NewGuid():N}"[..12];
        var spans = new ConcurrentBag<Activity>();
        var counts = new ConcurrentDictionary<string, long>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name == InstalmentFlowTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (Equals(a.GetTagItem("instalmentflow.provider"), provider)) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(activities);
        using var meters = new MeterListener { InstrumentPublished = (i, l) => { if (i.Meter.Name == InstalmentFlowTelemetry.MeterName) { l.EnableMeasurementEvents(i); } } };
        meters.SetMeasurementEventCallback<long>((i, value, tags, _) =>
        {
            var tagged = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (Equals(tagged.GetValueOrDefault("instalmentflow.provider"), provider))
            {
                var label = tagged.GetValueOrDefault("instalmentflow.outcome") ?? tagged.GetValueOrDefault("instalmentflow.status");
                counts.AddOrUpdate($"{i.Name}:{label}", value, (_, n) => n + value);
            }
        });
        meters.Start();

        var h = await Harness.CreateAsync(simulator: new SimulatedProvider("secret", provider: provider));
        await h.Payments.Routing.MapAsync(provider, Harness.Paybill, Harness.Tenant, Ct);
        h.Simulator.Pay(new SimulatedPayment { ProviderAccount = Harness.Paybill, Amount = Money.Kes(100m), AccountReference = Harness.Loan, Faults = SimulatedFaults.Duplicate });
        await h.DeliverAndProcessAsync();

        Assert.Equal(1, counts.GetValueOrDefault("instalmentflow.inbox.received:Stored"));
        Assert.Equal(1, counts.GetValueOrDefault("instalmentflow.inbox.received:Duplicate"));
        Assert.Equal(1, counts.GetValueOrDefault("instalmentflow.inbox.processed:Processed"));
        Assert.Contains(spans, s => s.DisplayName == "instalmentflow.inbox.process" && Equals(s.GetTagItem("instalmentflow.status"), "Processed"));
        Assert.Equal(2, spans.Count(s => s.DisplayName == "instalmentflow.inbox.receive"));
    }
}
