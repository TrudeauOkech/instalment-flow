using System.Globalization;
using BenchmarkDotNet.Running;
using InstalmentFlow.Benchmarks;

// dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks                     BenchmarkDotNet, in memory
// dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --postgres [s] [w]   NFR-4 on PostgreSQL (Docker), s seconds of load, w writers
if (args is ["--postgres", ..])
{
    var seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 30;
    var writers = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 16;
    await PostgresLoad.RunAsync(latencyPayments: 500, accounts: 64, writers, TimeSpan.FromSeconds(seconds));
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Workload).Assembly).Run(args);
return 0;
