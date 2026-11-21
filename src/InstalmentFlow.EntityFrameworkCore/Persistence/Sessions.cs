using System.Data.Common;
using InstalmentFlow.Domain;
using InstalmentFlow.EntityFrameworkCore.Schema;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>
/// A database transaction scoped to one tenant. It is either the store's own (one per command attempt, or one per
/// read outside a command) or a savepoint inside a transaction the host owns (<see cref="InstalmentFlowPostgres.Enlist"/>).
/// The tenant is set with <c>set_config(…, true)</c>, the equivalent of <c>SET LOCAL</c>, so it never outlives the
/// transaction and a pooled connection cannot leak it (doc 05 §4a).
/// </summary>
internal sealed class Session : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly string? _savepoint;
    private readonly bool _ownsConnection;
    private bool _completed;

    private Session(NpgsqlConnection connection, NpgsqlTransaction transaction, string? savepoint, bool ownsConnection, TenantId tenant)
    {
        _connection = connection;
        _transaction = transaction;
        _savepoint = savepoint;
        _ownsConnection = ownsConnection;
        Tenant = tenant;
    }

    public TenantId Tenant { get; }

    public NpgsqlConnection Connection => _connection;

    public NpgsqlTransaction Transaction => _transaction;

    /// <summary>Accounts as loaded in this session, the base for writing only changed rows at commit.</summary>
    public Dictionary<AccountId, CustomerAccount> LoadedAccounts { get; } = [];

    /// <summary>Unidentified funds as loaded in this session.</summary>
    public Dictionary<string, UnidentifiedFunds> LoadedPools { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Begins a session. Command attempts run at READ COMMITTED: the commit-time race guards must see rows that
    /// concurrent commands committed after the attempt began. A store-owned read outside a command
    /// (<paramref name="snapshot"/>) runs at REPEATABLE READ, so a query or Verify spanning many tables reads one
    /// consistent state.
    /// </summary>
    public static async Task<Session> BeginAsync(NpgsqlDataSource dataSource, TenantId tenant, CancellationToken ct, bool snapshot = false)
    {
        if (HostTransaction.Current is { } host)
        {
            var name = $"if_{Guid.NewGuid():N}";
            await host.Transaction.SaveAsync(name, ct).ConfigureAwait(false);
            var enlisted = new Session(host.Connection, host.Transaction, name, false, tenant);
            await enlisted.SetTenantAsync(ct).ConfigureAwait(false);
            return enlisted;
        }

        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(snapshot ? System.Data.IsolationLevel.RepeatableRead : System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var session = new Session(connection, transaction, null, true, tenant);
            await session.SetTenantAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public InstalmentFlowDbContext Context() => DbContexts.Create(_connection, _transaction);

    /// <summary>Commits the store's own transaction, or releases the savepoint in a host transaction (the host commits).</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_savepoint is null)
        {
            await _transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await _transaction.ReleaseAsync(_savepoint, ct).ConfigureAwait(false);
        }

        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed && _connection.State == System.Data.ConnectionState.Open)
            {
                if (_savepoint is null)
                {
                    await _transaction.RollbackAsync().ConfigureAwait(false);
                }
                else
                {
                    await _transaction.RollbackAsync(_savepoint).ConfigureAwait(false);
                }
            }
        }
        catch (NpgsqlException)
        {
            // The connection broke (for example it was terminated); there is nothing left to roll back.
        }
        finally
        {
            if (_ownsConnection)
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SetTenantAsync(CancellationToken ct)
    {
        var command = new NpgsqlCommand("select set_config(@name, @tenant, true)", _connection, _transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("name", SchemaSql.TenantSetting);
        command.Parameters.AddWithValue("tenant", Tenant.Value);
        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>The scope of one command attempt: a lazily opened session that every store call of the attempt shares.</summary>
internal sealed class CommandScope(NpgsqlDataSource dataSource, TenantId tenant) : IAsyncDisposable
{
    private static readonly AsyncLocal<CommandScope?> s_current = new();
    private Session? _session;
    private bool _disposed;

    public static CommandScope? Current => s_current.Value is { _disposed: false } scope ? scope : null;

    public TenantId Tenant { get; } = tenant;

    public static CommandScope Enter(NpgsqlDataSource dataSource, TenantId tenant)
    {
        var scope = new CommandScope(dataSource, tenant);
        s_current.Value = scope;
        return scope;
    }

    public async Task<Session> SessionAsync(CancellationToken ct) => _session ??= await Session.BeginAsync(dataSource, Tenant, ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A transaction owned by the host, which InstalmentFlow commands join through savepoints.</summary>
internal sealed class HostTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IDisposable
{
    private static readonly AsyncLocal<HostTransaction?> s_current = new();
    private readonly HostTransaction? _previous = s_current.Value;

    public static HostTransaction? Current => s_current.Value;

    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public static HostTransaction Enter(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction npgsql || npgsql.Connection is not { } connection)
        {
            throw new ArgumentException("The transaction must be an open Npgsql transaction.", nameof(transaction));
        }

        var host = new HostTransaction(connection, npgsql);
        s_current.Value = host;
        return host;
    }

    public void Dispose() => s_current.Value = _previous;
}
