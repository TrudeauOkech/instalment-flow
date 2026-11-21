using InstalmentFlow.EntityFrameworkCore;
using InstalmentFlow.Storage;
using InstalmentFlow.TestSupport;

namespace InstalmentFlow.Tests;

/// <summary>
/// Creates the engine under test. By default it is the in-memory store; with <c>IF_STORE=postgres</c> every test runs
/// against PostgreSQL instead (a fresh database per engine, as the application role under row-level security), so the
/// whole behavioural suite checks both stores (M7).
/// </summary>
internal static class TestEngine
{
    public static bool IsPostgres { get; } = string.Equals(Environment.GetEnvironmentVariable("IF_STORE"), "postgres", StringComparison.OrdinalIgnoreCase);

    public static InstalmentFlowFactory Create(Action<InstalmentFlowBuilder>? configure = null)
    {
        if (!IsPostgres)
        {
            return InstalmentFlowFactory.CreateInMemory(configure);
        }

        var database = PostgresServer.CreateDatabaseAsync().GetAwaiter().GetResult();
        return InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = database.Application }, configure).GetAwaiter().GetResult();
    }

    /// <summary>Scales a property test's iteration count down on PostgreSQL, where each engine is a database.</summary>
    public static int Iterations(int inMemory) => IsPostgres ? Math.Max(10, inMemory / 10) : inMemory;

    public static LedgerSnapshot Snapshot(this InstalmentFlowFactory factory, TenantId tenant) =>
        factory.LedgerStore.SnapshotAsync(tenant, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
}
