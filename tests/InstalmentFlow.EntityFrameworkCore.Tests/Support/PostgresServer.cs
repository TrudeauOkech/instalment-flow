using InstalmentFlow.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace InstalmentFlow.TestSupport;

/// <summary>A disposable PostgreSQL database for one test engine, with connection strings for each role.</summary>
/// <param name="Name">The database name.</param>
/// <param name="Application">The application role (no superuser, no BYPASSRLS), as the engine connects.</param>
/// <param name="Dispatcher">The outbox dispatcher role.</param>
/// <param name="Owner">The schema owner, for migrations and for tampering in tests.</param>
/// <param name="Superuser">The container's superuser.</param>
internal sealed record PostgresDatabase(string Name, string Application, string Dispatcher, string Owner, string Superuser);

/// <summary>
/// One PostgreSQL container per test run (Testcontainers). Roles mirror a production deployment: an owner that runs
/// migrations, an application role subject to row-level security, and a dispatcher role. A migrated template
/// database is cloned for every test engine, so tests are isolated and fast.
/// </summary>
internal static class PostgresServer
{
    public const string OwnerRole = "if_owner";
    public const string ApplicationRole = "if_app";
    public const string DispatcherRole = "if_dispatcher";
    private const string Password = "test-only-password";
    private const string Template = "if_template";

    private static readonly SemaphoreSlim s_create = new(1, 1);
    private static readonly Lazy<Task<PostgreSqlContainer>> s_container = new(StartAsync);
    private static int s_databases;

    /// <summary>A fresh, migrated database.</summary>
    public static async Task<PostgresDatabase> CreateDatabaseAsync()
    {
        var container = await s_container.Value.ConfigureAwait(false);
        var name = $"if_{Interlocked.Increment(ref s_databases)}";
        await s_create.WaitAsync().ConfigureAwait(false);
        try
        {
            await ExecuteAsync(container.GetConnectionString(), $"create database {name} template {Template} owner {OwnerRole}").ConfigureAwait(false);
        }
        finally
        {
            s_create.Release();
        }

        return Database(container, name);
    }

    /// <summary>An empty database with roles but no schema, for migration tests.</summary>
    public static async Task<PostgresDatabase> CreateEmptyDatabaseAsync()
    {
        var container = await s_container.Value.ConfigureAwait(false);
        var name = $"if_empty_{Interlocked.Increment(ref s_databases)}";
        await ExecuteAsync(container.GetConnectionString(), $"create database {name} owner {OwnerRole}").ConfigureAwait(false);
        return Database(container, name);
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var command = new NpgsqlCommand(sql, connection);
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static PostgresDatabase Database(PostgreSqlContainer container, string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name };
        string As(string role, bool pooled)
        {
            var b = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Username = role, Password = Password, Pooling = pooled };
            if (pooled)
            {
                // Many databases per run: idle connections are closed quickly.
                b.MinPoolSize = 0;
                b.MaxPoolSize = 20;
                b.ConnectionIdleLifetime = 2;
                b.ConnectionPruningInterval = 1;
            }

            return b.ConnectionString;
        }

        return new PostgresDatabase(name, As(ApplicationRole, true), As(DispatcherRole, true), As(OwnerRole, false), builder.ConnectionString);
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "max_connections=2000", "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off")
            .Build();
        await container.StartAsync().ConfigureAwait(false);

        var admin = container.GetConnectionString();
        await ExecuteAsync(admin, $"""
            create role {OwnerRole} login password '{Password}' nosuperuser nobypassrls createdb;
            create role {ApplicationRole} login password '{Password}' nosuperuser nobypassrls;
            create role {DispatcherRole} login password '{Password}' nosuperuser nobypassrls;
            """).ConfigureAwait(false);
        await ExecuteAsync(admin, $"create database {Template} owner {OwnerRole}").ConfigureAwait(false);   // not allowed in a multi-statement batch

        var template = Database(container, Template);
        await ExecuteAsync(new NpgsqlConnectionStringBuilder(template.Superuser).ConnectionString, $"alter schema public owner to {OwnerRole}").ConfigureAwait(false);
        await InstalmentFlowPostgres.MigrateAsync(template.Owner, new MigrationOptions { ApplicationRole = ApplicationRole, DispatcherRole = DispatcherRole }).ConfigureAwait(false);
        NpgsqlConnection.ClearAllPools();   // a template cannot be cloned while connections are open
        return container;
    }
}
