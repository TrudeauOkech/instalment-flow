using InstalmentFlow.EntityFrameworkCore.Schema;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>
/// The payments store on PostgreSQL. Every call is one short transaction. Intents and discrepancies are read and
/// written with the tenant set, under row-level security; routing, the inbox, the check schedule and run records are
/// not tenant data until an event is routed.
/// </summary>
internal sealed class PostgresPaymentsStore(NpgsqlDataSource dataSource) : IPaymentsStore
{
    // ----- Routing -----
    public async ValueTask<ProviderAccountMapping?> GetMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.ProviderAccounts.SingleOrDefaultAsync(m => m.Provider == provider && m.ProviderAccount == providerAccount, cancellationToken)
            .ConfigureAwait(false) is { } row ? ToMapping(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> TryAddMappingAsync(ProviderAccountMapping mapping, CancellationToken cancellationToken) =>
        await InsertAsync(null, db => db.ProviderAccounts.Add(new ProviderAccountRow
        {
            Provider = mapping.Provider, ProviderAccount = mapping.ProviderAccount, TenantId = mapping.Tenant.Value, CreatedAt = mapping.CreatedAt,
        }), "pk_tenant_provider_accounts", cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> RemoveMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.ProviderAccounts.Where(m => m.Provider == provider && m.ProviderAccount == providerAccount)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ProviderAccountMapping>> ListMappingsAsync(CancellationToken cancellationToken) =>
        await RunAsync<IReadOnlyList<ProviderAccountMapping>>(null, async db =>
            [.. (await db.ProviderAccounts.OrderBy(m => m.Provider).ThenBy(m => m.ProviderAccount).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToMapping)],
            cancellationToken).ConfigureAwait(false);

    // ----- Inbox -----
    public async ValueTask<bool> TryAddInboxAsync(InboxEntry entry, CancellationToken cancellationToken) =>
        await InsertAsync(null, db => db.Inbox.Add(ToRow(entry)), "ux_provider_inbox_dedupe", cancellationToken).ConfigureAwait(false);

    public async ValueTask<InboxEntry?> FindInboxAsync(string provider, string dedupeKey, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.Inbox.SingleOrDefaultAsync(e => e.Provider == provider && e.DedupeKey == dedupeKey, cancellationToken)
            .ConfigureAwait(false) is { } row ? ToEntry(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<InboxEntry>> ClaimInboxAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int max, CancellationToken cancellationToken) =>
        await RunAsync<IReadOnlyList<InboxEntry>>(null, async db =>
        {
            // Competing processors skip each other's rows instead of waiting; the lease covers a processor that dies.
            var rows = await db.Inbox.FromSqlInterpolated($"""
                update instalment_flow.provider_inbox set lease_until = {leaseUntil.ToUniversalTime()}
                where inbox_id in (
                  select inbox_id from instalment_flow.provider_inbox
                  where status = 0 and next_attempt_at <= {now.ToUniversalTime()} and (lease_until is null or lease_until <= {now.ToUniversalTime()})
                  order by received_at, inbox_id limit {max}
                  for update skip locked)
                returning *
                """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.OrderBy(r => r.ReceivedAt).ThenBy(r => r.InboxId).Select(ToEntry)];
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask UpdateInboxAsync(InboxEntry entry, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.Inbox.Where(e => e.InboxId == entry.Id).ExecuteUpdateAsync(e => e
            .SetProperty(x => x.Status, (short)entry.Status)
            .SetProperty(x => x.Attempts, entry.Attempts)
            .SetProperty(x => x.NextAttemptAt, entry.NextAttemptAt.ToUniversalTime())
            .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(x => x.ProcessedAt, entry.ProcessedAt)
            .SetProperty(x => x.Error, entry.Error)
            .SetProperty(x => x.TenantId, entry.Tenant == null ? null : entry.Tenant.Value)
            .SetProperty(x => x.PaymentId, entry.PaymentId == null ? null : entry.PaymentId.Value.Value), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<InboxEntry>> ListInboxAsync(InboxStatus status, int max, CancellationToken cancellationToken) =>
        await RunAsync<IReadOnlyList<InboxEntry>>(null, async db =>
            [.. (await db.Inbox.Where(e => e.Status == (short)status).OrderBy(e => e.ReceivedAt).ThenBy(e => e.InboxId).Take(max).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToEntry)],
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<InboxEntry?> GetInboxAsync(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.Inbox.SingleOrDefaultAsync(e => e.InboxId == id, cancellationToken).ConfigureAwait(false) is { } row ? ToEntry(row) : null,
            cancellationToken).ConfigureAwait(false);

    // ----- Intents -----
    public async ValueTask<bool> TryAddIntentAsync(IntentRecord intent, CancellationToken cancellationToken) =>
        await InsertAsync(intent.Tenant, db =>
        {
            db.Intents.Add(ToRow(intent));
            if (Check(intent) is { } check)
            {
                db.IntentChecks.Add(check);
            }
        }, "ux_payment_intents_key", cancellationToken).ConfigureAwait(false);

    public async ValueTask<IntentRecord?> GetIntentAsync(TenantId tenant, Guid intentId, CancellationToken cancellationToken) =>
        await RunAsync(tenant, async db => await db.Intents.SingleOrDefaultAsync(i => i.TenantId == tenant.Value && i.IntentId == intentId, cancellationToken)
            .ConfigureAwait(false) is { } row ? ToIntent(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IntentRecord?> FindIntentByKeyAsync(TenantId tenant, string idempotencyKey, CancellationToken cancellationToken) =>
        await RunAsync(tenant, async db => await db.Intents.SingleOrDefaultAsync(i => i.TenantId == tenant.Value && i.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false) is { } row ? ToIntent(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IntentRecord?> FindIntentByCorrelationAsync(TenantId tenant, string provider, string correlationId, CancellationToken cancellationToken) =>
        await RunAsync(tenant, async db => await db.Intents.SingleOrDefaultAsync(i => i.TenantId == tenant.Value && i.Provider == provider && i.CorrelationId == correlationId, cancellationToken)
            .ConfigureAwait(false) is { } row ? ToIntent(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> UpdateIntentAsync(IntentRecord intent, long expectedVersion, CancellationToken cancellationToken) =>
        await RunAsync(intent.Tenant, async db =>
        {
            var row = ToRow(intent with { Version = expectedVersion + 1 });
            var updated = await db.Intents.Where(i => i.TenantId == row.TenantId && i.IntentId == row.IntentId && i.Version == expectedVersion).ExecuteUpdateAsync(i => i
                .SetProperty(x => x.Status, row.Status)
                .SetProperty(x => x.CorrelationId, row.CorrelationId)
                .SetProperty(x => x.TransactionId, row.TransactionId)
                .SetProperty(x => x.PaymentId, row.PaymentId)
                .SetProperty(x => x.FailureReason, row.FailureReason)
                .SetProperty(x => x.UpdatedAt, row.UpdatedAt)
                .SetProperty(x => x.NextCheckAt, row.NextCheckAt)
                .SetProperty(x => x.StatusChecks, row.StatusChecks)
                .SetProperty(x => x.Version, row.Version), cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                return false;
            }

            await db.IntentChecks.Where(c => c.IntentId == row.IntentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (Check(intent) is { } check)
            {
                db.IntentChecks.Add(check);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<(TenantId Tenant, Guid IntentId)>> ListDueIntentsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken) =>
        await RunAsync<IReadOnlyList<(TenantId, Guid)>>(null, async db =>
        {
            var utc = now.ToUniversalTime();
            var due = await db.IntentChecks.Where(c => c.NextCheckAt <= utc).OrderBy(c => c.NextCheckAt).ThenBy(c => c.IntentId).Take(max)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. due.Select(c => (TenantId.FromString(c.TenantId), c.IntentId))];
        }, cancellationToken).ConfigureAwait(false);

    // ----- Reconciliation -----
    public async ValueTask<ReconciliationRunRecord?> LastRunAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        await RunAsync(null, async db => await db.ReconciliationRuns.Where(r => r.Provider == provider && r.ProviderAccount == providerAccount)
            .OrderByDescending(r => r.WindowEnd).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } r
            ? new ReconciliationRunRecord(r.RunId, r.Provider, r.ProviderAccount, r.WindowStart, r.WindowEnd, r.StartedAt, r.FinishedAt, r.Lines, r.Matched,
                r.MissingInEngine, r.MissingAtProvider, r.AmountMismatches, r.ReversedAtProvider, r.Unroutable)
            : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask AddRunAsync(ReconciliationRunRecord run, CancellationToken cancellationToken) =>
        await InsertAsync(null, db => db.ReconciliationRuns.Add(new ReconciliationRunRow
        {
            RunId = run.Id, Provider = run.Provider, ProviderAccount = run.ProviderAccount, WindowStart = run.From, WindowEnd = run.To, StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt, Lines = run.Lines, Matched = run.Matched, MissingInEngine = run.MissingInEngine, MissingAtProvider = run.MissingAtProvider,
            AmountMismatches = run.AmountMismatches, ReversedAtProvider = run.ReversedAtProvider, Unroutable = run.Unroutable,
        }), null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> TryAddDiscrepancyAsync(DiscrepancyRecord discrepancy, CancellationToken cancellationToken) =>
        await InsertAsync(discrepancy.Tenant, db => db.Discrepancies.Add(new DiscrepancyRow
        {
            TenantId = discrepancy.Tenant.Value, DiscrepancyId = discrepancy.Id, RunId = discrepancy.RunId, Provider = discrepancy.Provider,
            ProviderAccount = discrepancy.ProviderAccount, Kind = (short)discrepancy.Kind, TransactionId = discrepancy.TransactionId,
            StatementAmount = discrepancy.StatementAmount?.Amount, EngineAmount = discrepancy.EngineAmount?.Amount,
            Currency = (discrepancy.StatementAmount ?? discrepancy.EngineAmount)?.Currency.Code, PaymentId = discrepancy.PaymentId?.Value,
            DetectedAt = discrepancy.DetectedAt, ResolvedAt = discrepancy.ResolvedAt, Resolution = discrepancy.Resolution,
        }), "ux_reconciliation_discrepancies_line", cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<DiscrepancyRecord>> ListDiscrepanciesAsync(TenantId tenant, bool openOnly, CancellationToken cancellationToken) =>
        await RunAsync<IReadOnlyList<DiscrepancyRecord>>(tenant, async db =>
        {
            var rows = await db.Discrepancies.Where(d => d.TenantId == tenant.Value && (!openOnly || d.ResolvedAt == null))
                .OrderBy(d => d.DetectedAt).ThenBy(d => d.DiscrepancyId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(ToDiscrepancy)];
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> ResolveDiscrepancyAsync(TenantId tenant, Guid discrepancyId, DateTimeOffset at, string resolution, CancellationToken cancellationToken) =>
        await RunAsync(tenant, async db => await db.Discrepancies.Where(d => d.TenantId == tenant.Value && d.DiscrepancyId == discrepancyId && d.ResolvedAt == null)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.ResolvedAt, at.ToUniversalTime()).SetProperty(x => x.Resolution, resolution), cancellationToken).ConfigureAwait(false) > 0,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<PaymentsHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        await RunAsync(null, async db =>
        {
            var pending = db.Inbox.Where(e => e.Status == (short)InboxStatus.Pending);
            var ambiguous = db.IntentChecks.Where(c => c.Status == (short)PaymentIntentStatus.Ambiguous || c.Status == (short)PaymentIntentStatus.Created);
            var runs = await db.ReconciliationRuns.GroupBy(r => new { r.Provider, r.ProviderAccount })
                .Select(g => new { g.Key.Provider, g.Key.ProviderAccount, End = g.Max(r => r.WindowEnd), Finished = g.Max(r => r.FinishedAt) })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return new PaymentsHealth(
                await pending.CountAsync(cancellationToken).ConfigureAwait(false),
                await pending.MinAsync(e => (DateTimeOffset?)e.ReceivedAt, cancellationToken).ConfigureAwait(false),
                await db.Inbox.CountAsync(e => e.Status == (short)InboxStatus.Failed, cancellationToken).ConfigureAwait(false),
                await db.IntentChecks.CountAsync(cancellationToken).ConfigureAwait(false),
                await ambiguous.CountAsync(cancellationToken).ConfigureAwait(false),
                await ambiguous.MinAsync(c => (DateTimeOffset?)c.CreatedAt, cancellationToken).ConfigureAwait(false),
                [.. runs.OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.ProviderAccount, StringComparer.Ordinal)
                    .Select(r => new ReconciliationStatus(r.Provider, r.ProviderAccount, r.End, r.Finished))]);
        }, cancellationToken).ConfigureAwait(false);

    // ----- Transactions -----

    /// <summary>Runs <paramref name="work"/> in its own transaction; with a tenant, scoped to it like the ledger store.</summary>
    private async Task<T> RunAsync<T>(TenantId? tenant, Func<InstalmentFlowDbContext, Task<T>> work, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        if (tenant is not null)
        {
            var set = new NpgsqlCommand("select set_config(@name, @tenant, true)", connection, transaction);
            await using var setScope = set.ConfigureAwait(false);
            set.Parameters.AddWithValue("name", SchemaSql.TenantSetting);
            set.Parameters.AddWithValue("tenant", tenant.Value);
            await set.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        var db = DbContexts.Create(connection, transaction);
        await using var dbScope = db.ConfigureAwait(false);
        var result = await work(db).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Adds rows and saves; false if <paramref name="uniqueConstraint"/> refused them (someone stored it first).</summary>
    private async Task<bool> InsertAsync(TenantId? tenant, Action<InstalmentFlowDbContext> add, string? uniqueConstraint, CancellationToken ct)
    {
        try
        {
            return await RunAsync(tenant, async db =>
            {
                add(db);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (uniqueConstraint is not null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == uniqueConstraint)
        {
            return false;
        }
    }

    // ----- Mapping -----
    private static ProviderAccountMapping ToMapping(ProviderAccountRow r) => new(r.Provider, r.ProviderAccount, TenantId.FromString(r.TenantId), r.CreatedAt);

    private static InboxRow ToRow(InboxEntry e) => new()
    {
        InboxId = e.Id, Provider = e.Provider, DedupeKey = e.DedupeKey, Origin = (short)e.Origin, Kind = (short)e.Event.Kind, Event = EventJson.Serialize(e.Event),
        RawPayload = e.RawPayload, ReceivedAt = e.ReceivedAt, Status = (short)e.Status, Attempts = e.Attempts, NextAttemptAt = e.NextAttemptAt, LeaseUntil = e.LeaseUntil,
        ProcessedAt = e.ProcessedAt, Error = e.Error, TenantId = e.Tenant?.Value, PaymentId = e.PaymentId?.Value,
    };

    private static InboxEntry ToEntry(InboxRow r) => new(
        r.InboxId, r.Provider, r.DedupeKey, (InboxOrigin)r.Origin, EventJson.Deserialize(r.Event), r.RawPayload, r.ReceivedAt, (InboxStatus)r.Status, r.Attempts,
        r.NextAttemptAt, r.LeaseUntil, r.ProcessedAt, r.Error, r.TenantId is null ? null : TenantId.FromString(r.TenantId), r.PaymentId is { } p ? new PaymentId(p) : null);

    private static IntentRow ToRow(IntentRecord i) => new()
    {
        TenantId = i.Tenant.Value, IntentId = i.Id, IdempotencyKey = i.IdempotencyKey, Fingerprint = i.Fingerprint, Provider = i.Provider, ProviderAccount = i.ProviderAccount,
        Payer = i.Payer, Amount = i.Amount.Amount, Currency = i.Amount.Currency.Code, TargetKind = (short)i.TargetKind, ObligationId = i.ObligationId?.Value,
        AccountId = i.AccountId?.Value, AccountReference = i.AccountReference, Description = i.Description, Status = (short)i.Status, CorrelationId = i.CorrelationId,
        TransactionId = i.TransactionId, PaymentId = i.PaymentId?.Value, FailureReason = i.FailureReason, CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt,
        ExpiresAt = i.ExpiresAt, NextCheckAt = i.NextCheckAt, StatusChecks = i.StatusChecks, Version = i.Version,
    };

    private static IntentRecord ToIntent(IntentRow r) => new(
        TenantId.FromString(r.TenantId), r.IntentId, r.IdempotencyKey, r.Fingerprint, r.Provider, r.ProviderAccount, r.Payer, Money.Of(r.Amount, r.Currency),
        (PaymentTargetKind)r.TargetKind, r.ObligationId is { } o ? new ObligationId(o) : null, r.AccountId is { } a ? new AccountId(a) : null, r.AccountReference,
        r.Description, (PaymentIntentStatus)r.Status, r.CorrelationId, r.TransactionId, r.PaymentId is { } p ? new PaymentId(p) : null, r.FailureReason, r.CreatedAt,
        r.UpdatedAt, r.ExpiresAt, r.NextCheckAt, r.StatusChecks, r.Version);

    private static IntentCheckRow? Check(IntentRecord i) =>
        !i.IsTerminal && i.NextCheckAt is { } next
            ? new IntentCheckRow { IntentId = i.Id, TenantId = i.Tenant.Value, NextCheckAt = next, Status = (short)i.Status, CreatedAt = i.CreatedAt }
            : null;

    private static DiscrepancyRecord ToDiscrepancy(DiscrepancyRow d) => new(
        TenantId.FromString(d.TenantId), d.DiscrepancyId, d.RunId, d.Provider, d.ProviderAccount, (DiscrepancyKind)d.Kind, d.TransactionId,
        d.StatementAmount is { } s ? Money.Of(s, d.Currency!) : null, d.EngineAmount is { } e ? Money.Of(e, d.Currency!) : null,
        d.PaymentId is { } p ? new PaymentId(p) : null, d.DetectedAt, d.ResolvedAt, d.Resolution);
}
