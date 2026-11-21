using System.Collections.Immutable;
using InstalmentFlow.EntityFrameworkCore.Persistence;
using InstalmentFlow.EntityFrameworkCore.Schema;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore;

/// <summary>Options for <see cref="OutboxDispatcher"/>.</summary>
public sealed record OutboxDispatcherOptions
{
    /// <summary>The most events claimed at once.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>How long a claimed event is reserved for this dispatcher before another may take it.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="OutboxDispatcher.RunAsync"/> waits when there is nothing to publish.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait before retrying an event whose publishing failed.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Publishes outbox events (doc 05 §5): claims pending events with <c>FOR UPDATE SKIP LOCKED</c> and a lease,
/// publishes them outside the claim transaction, then marks them. Delivery is at least once. Events of one customer
/// account are published in commit order: an event waits while an earlier event of its account is pending. Run one
/// or more dispatchers with a role that has only the dispatcher grants (<see cref="MigrationOptions.DispatcherRole"/>).
/// </summary>
public sealed class OutboxDispatcher : IAsyncDisposable
{
    private static readonly string S = InstalmentFlowDbContext.Schema;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly OutboxDispatcherOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates a dispatcher.</summary>
    /// <param name="connectionString">The dispatcher role's connection string.</param>
    /// <param name="publisher">Delivers each event.</param>
    /// <param name="options">Batch size, lease and timing.</param>
    /// <param name="timeProvider">The clock, for tests.</param>
    public OutboxDispatcher(string connectionString, IIntegrationEventPublisher publisher, OutboxDispatcherOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(publisher);
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _publisher = publisher;
        _options = options ?? new OutboxDispatcherOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Claims and publishes one batch. Returns how many events were published.</summary>
    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimAsync(cancellationToken).ConfigureAwait(false);
        var published = 0;
        foreach (var row in claimed)
        {
            var integrationEvent = new IntegrationEvent(row.EventId, row.Type, row.TenantId, row.AccountId is { } a ? new AccountId(a) : null, row.OccurredAt,
                Json.Deserialize<Dictionary<string, string>>(row.Payload).ToImmutableSortedDictionary(StringComparer.Ordinal));
            try
            {
                await _publisher.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any publishing failure leaves the event for a later attempt.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                var backoff = TimeSpan.FromSeconds(Math.Min(_options.MaxBackoff.TotalSeconds, Math.Pow(2, row.Attempts))); // floating-point-allowed: retry timing, not money
                await MarkAsync(row.OutboxId, "set attempts = attempts + 1, next_attempt_at = @at, lease_until = null, last_error = @error",
                    _time.GetUtcNow() + backoff, ex.Message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await MarkAsync(row.OutboxId, "set status = 1, published_at = @at, lease_until = null, attempts = attempts + 1", _time.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
            published++;
        }

        return published;
    }

    /// <summary>Dispatches until <paramref name="cancellationToken"/> is cancelled, waiting <see cref="OutboxDispatcherOptions.PollInterval"/> when idle.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await DispatchAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    /// <summary>How long the oldest undelivered event has waited, or null when nothing is waiting; for health checks.</summary>
    public async Task<TimeSpan?> GetLagAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await DispatcherModeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var command = new NpgsqlCommand($"select min(occurred_at) from {S}.outbox where status = 0", connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        DateTimeOffset? oldest = null;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                oldest = await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return oldest is { } at ? _time.GetUtcNow() - at : null;
    }

    private async Task<List<Claimed>> ClaimAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await DispatcherModeAsync(connection, transaction, ct).ConfigureAwait(false);

        var claimed = new List<Claimed>();
        var select = new NpgsqlCommand($"""
            select o.outbox_id, o.tenant_id, o.event_id, o.account_id, o.type, o.payload::text, o.occurred_at, o.attempts
            from {S}.outbox o
            where o.status = 0 and o.next_attempt_at <= @now and (o.lease_until is null or o.lease_until < @now)
              and not exists (select 1 from {S}.outbox p where p.status = 0 and p.tenant_id = o.tenant_id and p.account_id = o.account_id and p.outbox_id < o.outbox_id)
            order by o.outbox_id
            limit @batch
            for update of o skip locked
            """, connection, transaction);
        await using (select.ConfigureAwait(false))
        {
            select.Parameters.AddWithValue("now", now);
            select.Parameters.AddWithValue("batch", _options.BatchSize);
            var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                claimed.Add(new Claimed(reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt32(7)));
            }
        }

        if (claimed.Count > 0)
        {
            var lease = new NpgsqlCommand($"update {S}.outbox set lease_until = @until where outbox_id = any(@ids)", connection, transaction);
            await using var leaseScope = lease.ConfigureAwait(false);
            lease.Parameters.AddWithValue("until", now + _options.Lease);
            lease.Parameters.AddWithValue("ids", claimed.Select(c => c.OutboxId).ToArray());
            await lease.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    private async Task MarkAsync(long outboxId, string set, DateTimeOffset at, string? error, CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await DispatcherModeAsync(connection, transaction, ct).ConfigureAwait(false);
        var command = new NpgsqlCommand($"update {S}.outbox {set} where outbox_id = @id", connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", outboxId);
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task DispatcherModeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var command = new NpgsqlCommand($"select set_config('{SchemaSql.DispatcherSetting}', 'on', true)", connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private sealed record Claimed(long OutboxId, string TenantId, Guid EventId, Guid? AccountId, string Type, string Payload, DateTimeOffset OccurredAt, int Attempts);
}
