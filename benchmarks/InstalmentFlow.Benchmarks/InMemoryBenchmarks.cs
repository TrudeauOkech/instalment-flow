using BenchmarkDotNet.Attributes;

namespace InstalmentFlow.Benchmarks;

/// <summary>Receive and auto-allocate, in memory: the engine's own cost without a database.</summary>
[MemoryDiagnoser]
[InvocationCount(200)]
public class ReceiveBenchmarks
{
    private IInstalmentFlow _flow = null!;
    private int _n;

    [IterationSetup]
    public void Setup()
    {
        _flow = InstalmentFlowFactory.CreateInMemory(Workload.Configure).ForTenant(Workload.Tenant);
        Workload.SeedCustomerAsync(_flow, "C1").GetAwaiter().GetResult();
        _n = 0;
    }

    [Benchmark(Description = "Receive + allocate (5 x 365 instalments)")]
    public Task<RecordPaymentResult> ReceiveAndAllocate() => _flow.Payments.RecordAsync(Workload.Payment("C1", _n++));
}

/// <summary>Verify throughput: how long the invariant checks take on a tenant with history.</summary>
[MemoryDiagnoser]
public class VerifyBenchmarks
{
    private IInstalmentFlow _flow = null!;

    [Params(1_000)]
    public int Payments { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _flow = InstalmentFlowFactory.CreateInMemory(Workload.Configure).ForTenant(Workload.Tenant);
        for (var c = 0; c < 10; c++)
        {
            Workload.SeedCustomerAsync(_flow, $"C{c}").GetAwaiter().GetResult();
        }

        for (var n = 0; n < Payments; n++)
        {
            _flow.Payments.RecordAsync(Workload.Payment($"C{n % 10}", n)).GetAwaiter().GetResult();
        }
    }

    [Benchmark(Description = "Verify (10 customers, 50 obligations, payments as given)")]
    public Task<VerificationReport> Verify() => _flow.Queries.VerifyAsync(VerifyScope.Tenant);
}
