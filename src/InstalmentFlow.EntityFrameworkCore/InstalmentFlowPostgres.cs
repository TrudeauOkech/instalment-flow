using System.Data.Common;
using InstalmentFlow.EntityFrameworkCore.Persistence;
using InstalmentFlow.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore;

/// <summary>How commands guard a customer account against concurrent changes (doc 05 §5).</summary>
public enum ConcurrencyMode
{
    /// <summary>
    /// Commands read without locks and commit with a version check; a conflict reloads and retries. The default:
    /// no locks are held while a strategy runs, and contention per account is normally low.
    /// </summary>
    Optimistic = 0,

    /// <summary>
    /// Each command attempt locks the account (or unidentified funds) row with <c>SELECT … FOR UPDATE</c> when it
    /// reads it, so concurrent commands for one hot account queue instead of retrying.
    /// </summary>
    PessimisticAccountLock = 1,
}

/// <summary>Options for the PostgreSQL store.</summary>
public sealed record PostgresStoreOptions
{
    /// <summary>
    /// The connection string of the application role. That role must not be a superuser and must not have
    /// <c>BYPASSRLS</c>, so row-level security applies to it (ADR-007).
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>How commands guard accounts against concurrent changes.</summary>
    public ConcurrencyMode ConcurrencyMode { get; init; } = ConcurrencyMode.Optimistic;
}

/// <summary>Roles to grant when migrating (doc 05 §4a). Roles are created by the database administrator.</summary>
public sealed record MigrationOptions
{
    /// <summary>The application role: reads and writes projections, appends facts, never updates or deletes them.</summary>
    public string? ApplicationRole { get; init; }

    /// <summary>The outbox dispatcher role: reads and marks outbox rows of every tenant, and nothing else.</summary>
    public string? DispatcherRole { get; init; }
}

/// <summary>PostgreSQL persistence for InstalmentFlow (doc 05).</summary>
public static class InstalmentFlowPostgres
{
    internal const string HistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Creates an engine backed by PostgreSQL. The schema must already be migrated (<see cref="MigrateAsync"/>);
    /// tenants added on the builder are created if they do not exist. The factory owns a connection pool for the
    /// life of the process.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database schema is missing, older or newer than this version expects.</exception>
    public static async Task<InstalmentFlowFactory> CreateFactoryAsync(
        PostgresStoreOptions options,
        Action<InstalmentFlowBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        EnsureLockstepVersions();
        var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        await EnsureSchemaAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var store = new PostgresStore(dataSource, options.ConcurrencyMode);
        return await InstalmentFlowFactory.CreateAsync(configure, store, store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the schema migrations and grants the configured roles. Run it explicitly, as the schema owner, at
    /// deployment time; it never runs on its own (doc 05 §7). Idempotent.
    /// </summary>
    public static async Task MigrateAsync(string ownerConnectionString, MigrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        var db = Owner(ownerConnectionString);
        await using var dbScope = db.ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var grants = (options?.ApplicationRole is { } app ? SchemaSql.GrantApplication(app) : string.Empty)
            + (options?.DispatcherRole is { } dispatcher ? SchemaSql.GrantDispatcher(dispatcher) : string.Empty);
        if (grants.Length > 0)
        {
            await db.Database.ExecuteSqlRawAsync(grants, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The migrations as one idempotent SQL script, for deployments that review and apply SQL themselves.</summary>
    public static string GenerateMigrationScript()
    {
        using var db = new InstalmentFlowDbContext(new DbContextOptionsBuilder<InstalmentFlowDbContext>()
            .UseNpgsql(o => o.MigrationsHistoryTable(HistoryTable, InstalmentFlowDbContext.Schema)).Options);
        return db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>
    /// Makes InstalmentFlow commands on this async flow join <paramref name="transaction"/>, a transaction the host
    /// owns, for example its <c>DbContext</c>'s (<c>db.Database.CurrentTransaction.GetDbTransaction()</c>). Each
    /// command attempt runs in a savepoint; nothing is committed until the host commits. Dispose the result to stop.
    /// </summary>
    /// <exception cref="ArgumentException">The transaction is not an open Npgsql transaction.</exception>
    public static IDisposable Enlist(DbTransaction transaction) => HostTransaction.Enter(transaction);

    internal static InstalmentFlowDbContext Owner(string connectionString) =>
        new(new DbContextOptionsBuilder<InstalmentFlowDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(HistoryTable, InstalmentFlowDbContext.Schema))
            .Options);

    /// <summary>
    /// The store implements the core's internal storage ports (ADR-017), so both packages must come from the same
    /// release. A mismatch fails here, clearly, instead of later with missing members.
    /// </summary>
    private static void EnsureLockstepVersions()
    {
        static string Version(System.Reflection.Assembly assembly)
        {
            var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? string.Empty;
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        var core = Version(typeof(InstalmentFlowFactory).Assembly);
        var store = Version(typeof(InstalmentFlowPostgres).Assembly);
        if (!string.Equals(core, store, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"InstalmentFlow.EntityFrameworkCore {store} needs InstalmentFlow {store}, but {core} is loaded. Use the same version of both packages.");
        }
    }

    /// <summary>Refuses to run against a schema this version does not match (doc 05 §7).</summary>
    private static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var applied = new List<string>();
        try
        {
            var command = new NpgsqlCommand(
                $"select \"MigrationId\" from {InstalmentFlowDbContext.Schema}.{HistoryTable} order by 1", connection);
            await using var commandScope = command.ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                applied.Add(reader.GetString(0));
            }
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            throw new InvalidOperationException("The InstalmentFlow schema is not installed; run InstalmentFlowPostgres.MigrateAsync first.", ex);
        }

        var db = Owner("Host=unused");
        await using var dbScope = db.ConfigureAwait(false);
        var known = db.Database.GetMigrations().ToList();
        if (applied.Except(known).FirstOrDefault() is { } newer)
        {
            throw new InvalidOperationException($"The database schema has migration {newer}, which this version of InstalmentFlow does not know. Upgrade the package.");
        }

        if (known.Except(applied).FirstOrDefault() is { } missing)
        {
            throw new InvalidOperationException($"The database schema is missing migration {missing}; run InstalmentFlowPostgres.MigrateAsync.");
        }
    }
}
