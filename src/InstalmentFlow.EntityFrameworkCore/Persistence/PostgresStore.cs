using InstalmentFlow.Domain;
using InstalmentFlow.EntityFrameworkCore.Schema;
using InstalmentFlow.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>Creates DbContexts on an existing connection and transaction.</summary>
internal static class DbContexts
{
    public static InstalmentFlowDbContext Create(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var context = new InstalmentFlowDbContext(new DbContextOptionsBuilder<InstalmentFlowDbContext>()
            .UseNpgsql(connection, o => o.MigrationsHistoryTable(InstalmentFlowPostgres.HistoryTable, InstalmentFlowDbContext.Schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
        context.Database.UseTransaction(transaction);
        return context;
    }
}

/// <summary>
/// The PostgreSQL store (doc 05): the same contract as the in-memory store, enforced again by the database. Every
/// call runs in a tenant-scoped transaction: the command scope's when a command is running, otherwise its own.
/// </summary>
internal sealed partial class PostgresStore(NpgsqlDataSource dataSource, ConcurrencyMode mode, int accountCacheSize = 1024) : ITenantStore, ILedgerStore
{
    private readonly AccountCache? _cache = accountCacheSize > 0 ? new AccountCache(accountCacheSize) : null;

    /// <summary>Test hook: on every cache hit, also reload the account and fail if the two differ.</summary>
    internal bool CheckCacheOnHit { get; set; }

    /// <summary>Test hook: how many account loads the cache served.</summary>
    internal int CacheHits => _cacheHits;

    private int _cacheHits;

    /// <summary>Test hook: runs after a commit's statements and before its transaction commits (failure injection).</summary>
    internal Func<Session, CancellationToken, Task>? BeforeCommit { get; set; }

    /// <summary>Test hook: runs after a command's reversal-reference lookup, to widen a race window on purpose.</summary>
    internal Func<CancellationToken, Task>? AfterReversalLookup { get; set; }

    public IAsyncDisposable? EnterCommandScope(TenantId tenantId) => CommandScope.Enter(dataSource, tenantId);

    /// <summary>Runs <paramref name="work"/> in the current command scope's session, or in a short transaction of its own.</summary>
    private async Task<T> InTenantAsync<T>(TenantId tenant, Func<Session, InstalmentFlowDbContext, Task<T>> work, CancellationToken ct, bool commit = false)
    {
        if (CommandScope.Current is { } scope && scope.Tenant == tenant)
        {
            var session = await scope.SessionAsync(ct).ConfigureAwait(false);
            var db = session.Context();
            await using var dbScope = db.ConfigureAwait(false);
            return await work(session, db).ConfigureAwait(false);
        }

        var own = await Session.BeginAsync(dataSource, tenant, ct, snapshot: !commit).ConfigureAwait(false);
        await using var ownScope = own.ConfigureAwait(false);
        var context = own.Context();
        await using var contextScope = context.ConfigureAwait(false);
        var result = await work(own, context).ConfigureAwait(false);
        if (commit)
        {
            await own.CompleteAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<TenantRecord?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, (_, db) => LoadTenantAsync(db, tenantId.Value, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async ValueTask CreateAsync(TenantRecord tenant, CancellationToken cancellationToken)
    {
        try
        {
            await InTenantAsync(tenant.Id, async (_, db) =>
            {
                db.Tenants.Add(new TenantRow { TenantId = tenant.Id.Value, BaseCurrency = tenant.BaseCurrency.Code, TimeZone = tenant.TimeZone });
                db.PolicyVersions.AddRange(tenant.Policies.Select(p => Mapping.ToRow(tenant.Id.Value, p)));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken, commit: true).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new RuleViolationException(ErrorCodes.DuplicateTenant, $"Tenant '{tenant.Id}' already exists.");
        }
    }

    public async ValueTask<TenantRecord> AppendPolicyAsync(TenantId tenantId, Func<TenantRecord, TenantPolicyVersion> next, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (session, db) =>
        {
            var lockTenant = new NpgsqlCommand($"select 1 from {InstalmentFlowDbContext.Schema}.tenants where tenant_id = @t for update", session.Connection, session.Transaction);
            await using (lockTenant.ConfigureAwait(false))
            {
                lockTenant.Parameters.AddWithValue("t", tenantId.Value);
                await lockTenant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var current = await LoadTenantAsync(db, tenantId.Value, cancellationToken).ConfigureAwait(false) ?? throw new UnknownTenantException(tenantId);
            var version = next(current);
            db.PolicyVersions.Add(Mapping.ToRow(tenantId.Value, version));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return current with { Policies = current.Policies.Add(version) };
        }, cancellationToken, commit: true).ConfigureAwait(false);

    private static async Task<TenantRecord?> LoadTenantAsync(InstalmentFlowDbContext db, string tenant, CancellationToken ct)
    {
        var row = await db.Tenants.SingleOrDefaultAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var policies = await db.PolicyVersions.Where(p => p.TenantId == tenant).ToListAsync(ct).ConfigureAwait(false);
        return Mapping.ToTenant(row, policies);
    }

    public async ValueTask<CustomerAccount?> LoadAccountAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (session, db) =>
        {
            if (mode == ConcurrencyMode.PessimisticAccountLock && CommandScope.Current is not null)
            {
                await LockAsync(session, "accounts", "account_id", accountId.Value, cancellationToken).ConfigureAwait(false);
            }

            if (_cache is not null && _cache.TryGet(tenantId, accountId, out var cached))
            {
                var current = await db.Accounts.Where(a => a.TenantId == tenantId.Value && a.AccountId == accountId.Value).Select(a => (long?)a.Version)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (current == cached.Version)
                {
                    if (CheckCacheOnHit)
                    {
                        var fresh = await LoadAccountRowsAsync(db, tenantId.Value, accountId.Value, cancellationToken).ConfigureAwait(false);
                        var still = await db.Accounts.Where(a => a.TenantId == tenantId.Value && a.AccountId == accountId.Value).Select(a => (long?)a.Version)
                            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                        if (still == cached.Version && fresh?.Version == cached.Version)
                        {
                            RequireSame(cached, fresh);   // compared only when no commit landed during the reload
                        }
                    }

                    Interlocked.Increment(ref _cacheHits);
                    session.LoadedAccounts[accountId] = cached;
                    return cached;
                }
            }

            var account = await LoadAccountRowsAsync(db, tenantId.Value, accountId.Value, cancellationToken).ConfigureAwait(false);
            if (account is not null)
            {
                await RequireUntornAsync(db.Accounts.Where(a => a.TenantId == tenantId.Value && a.AccountId == accountId.Value).Select(a => a.Version),
                    account.Version, accountId, cancellationToken).ConfigureAwait(false);
                session.LoadedAccounts[account.Id] = account;
                _cache?.Set(account);
            }

            return account;
        }, cancellationToken).ConfigureAwait(false);

    private static async Task<CustomerAccount?> LoadAccountRowsAsync(InstalmentFlowDbContext db, string tenant, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.TenantId == tenant && a.AccountId == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        var obligations = await db.Obligations.Where(o => o.TenantId == tenant && o.AccountId == accountId).OrderBy(o => o.OpenedAt).ThenBy(o => o.ObligationId)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = obligations.Select(o => o.ObligationId).ToList();
        var rows = new AccountRows(
            account,
            obligations,
            await db.OriginatedComponents.Where(c => c.TenantId == tenant && ids.Contains(c.ObligationId)).ToListAsync(ct).ConfigureAwait(false),
            await db.PlanVersions.Where(p => p.TenantId == tenant && ids.Contains(p.ObligationId)).OrderBy(p => p.ObligationId).ThenBy(p => p.Version).ToListAsync(ct).ConfigureAwait(false),
            await db.Instalments.Where(i => i.TenantId == tenant && ids.Contains(i.ObligationId)).OrderBy(i => i.ObligationId).ThenBy(i => i.PlanVersion).ThenBy(i => i.Sequence)
                .ToListAsync(ct).ConfigureAwait(false),
            await db.InstalmentComponents.Where(c => c.TenantId == tenant && ids.Contains(c.ObligationId))
                .OrderBy(c => c.ObligationId).ThenBy(c => c.PlanVersion).ThenBy(c => c.Sequence).ThenBy(c => c.Component).ThenBy(c => c.ChargeCode).ToListAsync(ct).ConfigureAwait(false),
            await db.FundsLots.Where(l => l.TenantId == tenant && l.AccountId == accountId).ToListAsync(ct).ConfigureAwait(false));
        return Mapping.ToAccount(rows);
    }

    public async ValueTask<UnidentifiedFunds> LoadUnidentifiedAsync(TenantId tenantId, string source, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (session, db) =>
        {
            if (mode == ConcurrencyMode.PessimisticAccountLock && CommandScope.Current is not null)
            {
                var lockPool = new NpgsqlCommand(
                    $"select 1 from {InstalmentFlowDbContext.Schema}.unidentified_pools where tenant_id = @t and source = @s for update", session.Connection, session.Transaction);
                await using var lockPoolScope = lockPool.ConfigureAwait(false);
                lockPool.Parameters.AddWithValue("t", tenantId.Value);
                lockPool.Parameters.AddWithValue("s", source);
                await lockPool.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var pool = await LoadPoolAsync(db, tenantId.Value, source, cancellationToken).ConfigureAwait(false);
            if (pool.Version != CustomerAccount.NewVersion)
            {
                await RequireUntornAsync(db.Pools.Where(p => p.TenantId == tenantId.Value && p.Source == source).Select(p => p.Version),
                    pool.Version, default, cancellationToken).ConfigureAwait(false);
            }

            session.LoadedPools[source] = pool;
            return pool;
        }, cancellationToken).ConfigureAwait(false);

    private static async Task<UnidentifiedFunds> LoadPoolAsync(InstalmentFlowDbContext db, string tenant, string source, CancellationToken ct)
    {
        var row = await db.Pools.SingleOrDefaultAsync(p => p.TenantId == tenant && p.Source == source, ct).ConfigureAwait(false);
        var lots = await db.FundsLots.Where(l => l.TenantId == tenant && l.PoolSource == source).OrderBy(l => l.Ordinal).ToListAsync(ct).ConfigureAwait(false);
        return new UnidentifiedFunds(tenant, source, row?.Version ?? CustomerAccount.NewVersion, [.. lots.Select(Mapping.ToLot)]);
    }

    /// <summary>
    /// An aggregate is read with several statements, each of which sees the latest commit (READ COMMITTED). Every
    /// commit bumps the aggregate's version, so an unchanged version after the last statement proves the rows form
    /// one state; otherwise the attempt is retried rather than planned from a torn read.
    /// </summary>
    private static async Task RequireUntornAsync(IQueryable<long> version, long loaded, AccountId accountId, CancellationToken ct)
    {
        if (await version.SingleOrDefaultAsync(ct).ConfigureAwait(false) != loaded)
        {
            throw new ConcurrencyConflictException(accountId, loaded);
        }
    }

    private static void RequireSame(CustomerAccount cached, CustomerAccount fresh)
    {
        var (a, b) = (Mapping.ToRows(cached), Mapping.ToRows(fresh));
        static string? Diff<T>(string name, IReadOnlyList<T> x, IReadOnlyList<T> y)
        {
            if (x.Count != y.Count)
            {
                return $"{name}: {x.Count} cached, {y.Count} stored";
            }

            var i = Enumerable.Range(0, x.Count).FirstOrDefault(k => !Equals(x[k], y[k]), -1);
            return i < 0 ? null : $"{name}[{i}]: cached {x[i]} / stored {y[i]}";
        }

        // Schedule rows are compared as sets (their order carries no meaning); lots in order (their order is the insertion order).
        static List<T> Sorted<T>(IEnumerable<T> rows) => [.. rows.OrderBy(r => r!.ToString(), StringComparer.Ordinal)];
        var difference = Diff("account", [a.Account], [b.Account]) ?? Diff("obligations", Sorted(a.Obligations), Sorted(b.Obligations))
            ?? Diff("originated", Sorted(a.Originated), Sorted(b.Originated)) ?? Diff("plans", Sorted(a.Plans), Sorted(b.Plans))
            ?? Diff("instalments", Sorted(a.Instalments), Sorted(b.Instalments)) ?? Diff("components", Sorted(a.Components), Sorted(b.Components))
            ?? Diff("lots", a.Lots, b.Lots);
        if (difference is not null)
        {
            throw new InvalidOperationException($"The cached account {cached.Id} differs from the database at version {cached.Version}: {difference}");
        }
    }

    private static async Task LockAsync(Session session, string table, string column, Guid id, CancellationToken ct)
    {
        var command = new NpgsqlCommand(
            $"select 1 from {InstalmentFlowDbContext.Schema}.{table} where tenant_id = @t and {column} = @id for update", session.Connection, session.Transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("t", session.Tenant.Value);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<AccountId?> FindAccountAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var id = await db.Accounts.Where(a => a.TenantId == tenantId.Value && a.ExternalId == externalId).Select(a => (Guid?)a.AccountId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return id is { } found ? new AccountId(found) : (AccountId?)null;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ObligationLocation?> FindObligationAsync(TenantId tenantId, ObligationLookup lookup, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var tenant = tenantId.Value;
            var query = db.Obligations.Where(o => o.TenantId == tenant);
            ObligationRow? row = null;
            if (lookup.Id is { } id)
            {
                row = await query.SingleOrDefaultAsync(o => o.ObligationId == id.Value, cancellationToken).ConfigureAwait(false);
            }

            if (row is null && lookup.ExternalId is { } externalId)
            {
                row = await query.SingleOrDefaultAsync(o => o.ExternalId == externalId, cancellationToken).ConfigureAwait(false);
            }

            if (row is null && lookup.PaymentReference is { } reference)
            {
                row = await query.SingleOrDefaultAsync(o => o.PaymentReference == reference, cancellationToken).ConfigureAwait(false);
            }

            return row is null ? (ObligationLocation?)null : new ObligationLocation(new AccountId(row.AccountId), new ObligationId(row.ObligationId));
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PaymentRecord?> GetPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.Payments.SingleOrDefaultAsync(p => p.TenantId == tenantId.Value && p.PaymentId == paymentId.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToPayment(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PaymentRecord?> FindPaymentAsync(TenantId tenantId, ExternalReference source, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.Payments.SingleOrDefaultAsync(p => p.TenantId == tenantId.Value && p.Source == source.Source && p.ExternalRef == source.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToPayment(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<PaymentRecord>> ListPaymentsAsync(TenantId tenantId, string source, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var rows = db.Payments.Where(p => p.TenantId == tenantId.Value && p.Source == source);
            if (from is { } start)
            {
                var utc = start.ToUniversalTime();
                rows = rows.Where(p => p.ValueAt >= utc);
            }

            if (to is { } end)
            {
                var utc = end.ToUniversalTime();
                rows = rows.Where(p => p.ValueAt < utc);
            }

            return (IReadOnlyList<PaymentRecord>)[.. (await rows.OrderBy(p => p.ValueAt).ThenBy(p => p.PaymentId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToPayment)];
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PaymentAssignment?> GetAssignmentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.Assignments.SingleOrDefaultAsync(a => a.TenantId == tenantId.Value && a.PaymentId == paymentId.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToAssignment(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PaymentExceptionRecord?> GetExceptionAsync(TenantId tenantId, PaymentExceptionId exceptionId, CancellationToken cancellationToken) =>
        (await ExceptionsAsync(tenantId, e => e.ExceptionId == exceptionId.Value, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public async ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListExceptionsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await ExceptionsAsync(tenantId, _ => true, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListPaymentExceptionsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await ExceptionsAsync(tenantId, e => e.PaymentId == paymentId.Value, cancellationToken).ConfigureAwait(false);

    private async Task<List<PaymentExceptionRecord>> ExceptionsAsync(TenantId tenantId, System.Linq.Expressions.Expression<Func<PaymentExceptionRow, bool>> filter, CancellationToken ct) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var tenant = tenantId.Value;
            var rows = await db.PaymentExceptions.Where(e => e.TenantId == tenant).Where(filter).ToListAsync(ct).ConfigureAwait(false);
            var ids = rows.Select(r => r.ExceptionId).ToList();
            var resolutions = await db.ExceptionResolutions.Where(r => r.TenantId == tenant && ids.Contains(r.ExceptionId)).ToDictionaryAsync(r => r.ExceptionId, ct).ConfigureAwait(false);
            return rows.Select(r => Mapping.ToException(r, resolutions.GetValueOrDefault(r.ExceptionId))).OrderBy(e => e.OpenedAt).ThenBy(e => e.Id.Value).ToList();
        }, ct).ConfigureAwait(false);

    public async ValueTask<IdempotencyRecord?> GetIdempotencyAsync(TenantId tenantId, string key, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.IdempotencyKeys.SingleOrDefaultAsync(k => k.TenantId == tenantId.Value && k.Key == key, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToIdempotency(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<AllocationRecord>> ListAllocationsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            (await InRecordingOrder(db, db.Allocations.Where(a => a.TenantId == tenantId.Value && a.PaymentId == paymentId.Value)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(Mapping.ToAllocation).ToList(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<AllocationRecord>> ListAccountAllocationsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            (await InRecordingOrder(db, db.Allocations.Where(a => a.TenantId == tenantId.Value && a.AccountId == accountId.Value)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(Mapping.ToAllocation).ToList(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<AllocationReversalRecord>> ListAccountAllocationReversalsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            (await InRecordingOrder(db, db.AllocationReversals.Where(a => a.TenantId == tenantId.Value && a.AccountId == accountId.Value)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(Mapping.ToUnwind).ToList(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<PaymentReversalRecord>> ListPaymentReversalsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            (await InRecordingOrder(db, db.PaymentReversals.Where(r => r.TenantId == tenantId.Value && r.PaymentId == paymentId.Value)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(Mapping.ToReversal).ToList(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ReversalLookup?> FindReversalAsync(TenantId tenantId, ExternalReference reference, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (session, db) =>
        {
            var tenant = tenantId.Value;
            if (CommandScope.Current is not null)
            {
                // Concurrent deliveries of one reversal queue here until the first commits, then see it and report a
                // duplicate, instead of planning against the payment it already reversed. Held until the attempt ends.
                var command = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@k, 0))", session.Connection, session.Transaction);
                await using var commandScope = command.ConfigureAwait(false);
                command.Parameters.AddWithValue("k", $"reversal|{tenant}|{reference.Source}|{reference.Value}");
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var reserved = await db.ReversalReferences.SingleOrDefaultAsync(r => r.TenantId == tenant && r.Source == reference.Source && r.Value == reference.Value, cancellationToken).ConfigureAwait(false);
            if (reserved is null)
            {
                if (AfterReversalLookup is { } hook)
                {
                    await hook(cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            if (await db.PendingReversals.SingleOrDefaultAsync(p => p.TenantId == tenant && p.ReversalId == reserved.ReversalId, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                return new ReversalLookup(null, Mapping.ToPending(pending));
            }

            var recorded = await db.PaymentReversals.SingleAsync(r => r.TenantId == tenant && r.ReversalId == reserved.ReversalId, cancellationToken).ConfigureAwait(false);
            return new ReversalLookup(Mapping.ToReversal(recorded), null);
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<PendingReversal>> ListPendingReversalsAsync(TenantId tenantId, ExternalReference paymentSource, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            (await db.PendingReversals.Where(p => p.TenantId == tenantId.Value && p.PaymentSource == paymentSource.Source && p.PaymentRef == paymentSource.Value)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(Mapping.ToPending).OrderBy(p => p.At).ThenBy(p => p.Id.Value).ToList(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ChargeRecord?> GetChargeAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.Charges.SingleOrDefaultAsync(c => c.TenantId == tenantId.Value && c.ChargeId == chargeId.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToCharge(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ChargeReversalRecord?> GetChargeReversalAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.ChargeReversals.SingleOrDefaultAsync(c => c.TenantId == tenantId.Value && c.ChargeId == chargeId.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToChargeReversal(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<RefundRecord?> GetRefundAsync(TenantId tenantId, RefundId refundId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
            await db.Refunds.SingleOrDefaultAsync(r => r.TenantId == tenantId.Value && r.RefundId == refundId.Value, cancellationToken).ConfigureAwait(false) is { } row
                ? Mapping.ToRefund(row) : null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<RefundRecord>> ListPaymentRefundsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var probe = Json.Serialize(new[] { new { paymentId = paymentId.Value } });
            var rows = await db.Refunds.Where(r => r.TenantId == tenantId.Value && EF.Functions.JsonContains(r.Portions, probe)).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(Mapping.ToRefund).OrderBy(r => r.RequestedAt).ThenBy(r => r.Id.Value).ToList();
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Orders facts as they were recorded: by their journal transaction's sequence, then their position in its commit.</summary>
    private static IQueryable<TRow> InRecordingOrder<TRow>(InstalmentFlowDbContext db, IQueryable<TRow> rows)
        where TRow : class, IRecordedFact =>
        from row in rows
        join tx in db.JournalTransactions on new { row.TenantId, row.JournalTxId } equals new { tx.TenantId, tx.JournalTxId }
        orderby tx.Seq, row.Position
        select row;

    public async ValueTask<AccountHistory> LoadHistoryAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var tenant = tenantId.Value;
            var id = accountId.Value;
            var transactions = await db.JournalTransactions.Where(t => t.TenantId == tenant && t.AccountId == id).OrderBy(t => t.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
            var txIds = transactions.Select(t => t.JournalTxId).ToList();
            var lines = (await db.LedgerEntries.Where(l => l.TenantId == tenant && txIds.Contains(l.JournalTxId)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToLookup(l => l.JournalTxId);
            var assignments = await db.Assignments.Where(a => a.TenantId == tenant && a.AccountId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var assigned = assignments.Select(a => a.PaymentId).ToList();
            var charges = await db.Charges.Where(c => c.TenantId == tenant && c.AccountId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var chargeIds = charges.Select(c => c.ChargeId).ToList();
            return new AccountHistory(
                [.. transactions.Select(t => new PositionedTransaction(t.Seq, Mapping.ToJournal(t, lines[t.JournalTxId])))],
                [.. (await db.Payments.Where(p => p.TenantId == tenant && (p.AccountId == id || assigned.Contains(p.PaymentId))).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToPayment)],
                [.. assignments.Select(Mapping.ToAssignment)],
                [.. (await InRecordingOrder(db, db.Allocations.Where(a => a.TenantId == tenant && a.AccountId == id)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToAllocation)],
                [.. (await InRecordingOrder(db, db.AllocationReversals.Where(a => a.TenantId == tenant && a.AccountId == id)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToUnwind)],
                [.. (await InRecordingOrder(db, db.PaymentReversals.Where(r => r.TenantId == tenant && r.AccountId == id)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToReversal)],
                [.. charges.Select(Mapping.ToCharge)],
                [.. (await db.ChargeReversals.Where(c => c.TenantId == tenant && chargeIds.Contains(c.ChargeId)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToChargeReversal)],
                [.. (await db.Waivers.Where(w => w.TenantId == tenant && w.AccountId == id).OrderBy(w => w.At).ThenBy(w => w.WaiverId).ThenBy(w => w.Line).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToWaiver)],
                [.. (await db.Refunds.Where(r => r.TenantId == tenant && r.AccountId == id).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToRefund)]);
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<PositionedTransaction>> ReadJournalAsync(TenantId tenantId, long after, int max, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var tenant = tenantId.Value;
            var transactions = await db.JournalTransactions.Where(t => t.TenantId == tenant && t.Seq > after).OrderBy(t => t.Seq).Take(max).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = transactions.Select(t => t.JournalTxId).ToList();
            var lines = (await db.LedgerEntries.Where(l => l.TenantId == tenant && ids.Contains(l.JournalTxId)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(l => l.JournalTxId);
            return transactions.Select(t => new PositionedTransaction(t.Seq, Mapping.ToJournal(t, lines[t.JournalTxId]))).ToList();
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<LedgerSnapshot> SnapshotAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, async (_, db) =>
        {
            var tenant = tenantId.Value;
            var record = await LoadTenantAsync(db, tenant, cancellationToken).ConfigureAwait(false) ?? throw new UnknownTenantException(tenantId);
            var accounts = new List<CustomerAccount>();
            foreach (var id in await db.Accounts.Where(a => a.TenantId == tenant).OrderBy(a => a.OpenedAt).Select(a => a.AccountId).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                accounts.Add((await LoadAccountRowsAsync(db, tenant, id, cancellationToken).ConfigureAwait(false))!);
            }

            var pools = new List<UnidentifiedFunds>();
            foreach (var source in await db.Pools.Where(p => p.TenantId == tenant).Select(p => p.Source).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                pools.Add(await LoadPoolAsync(db, tenant, source, cancellationToken).ConfigureAwait(false));
            }

            var transactions = await db.JournalTransactions.Where(t => t.TenantId == tenant).OrderBy(t => t.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
            var lines = (await db.LedgerEntries.Where(l => l.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(l => l.JournalTxId);
            var resolutions = await db.ExceptionResolutions.Where(r => r.TenantId == tenant).ToDictionaryAsync(r => r.ExceptionId, cancellationToken).ConfigureAwait(false);
            var waivers = await db.Waivers.Where(w => w.TenantId == tenant).OrderBy(w => w.At).ThenBy(w => w.WaiverId).ThenBy(w => w.Line).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new LedgerSnapshot(
                record,
                [.. accounts],
                [.. pools],
                [.. (await db.Payments.Where(p => p.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToPayment)],
                [.. (await db.Assignments.Where(a => a.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToAssignment)],
                [.. (await InRecordingOrder(db, db.Allocations.Where(a => a.TenantId == tenant)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToAllocation)],
                [.. (await InRecordingOrder(db, db.AllocationReversals.Where(a => a.TenantId == tenant)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToUnwind)],
                [.. (await InRecordingOrder(db, db.PaymentReversals.Where(r => r.TenantId == tenant)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToReversal)],
                [.. (await db.PendingReversals.Where(p => p.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToPending)],
                [.. (await db.Charges.Where(c => c.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToCharge)],
                [.. (await db.ChargeReversals.Where(c => c.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToChargeReversal)],
                [.. waivers.Select(Mapping.ToWaiver)],
                [.. (await db.Refunds.Where(r => r.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToRefund)],
                [.. (await db.PaymentExceptions.Where(e => e.TenantId == tenant).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(e => Mapping.ToException(e, resolutions.GetValueOrDefault(e.ExceptionId)))],
                [.. transactions.Select(t => Mapping.ToJournal(t, lines[t.JournalTxId]))],
                [.. (await db.Outbox.Where(o => o.TenantId == tenant).OrderBy(o => o.OutboxId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Mapping.ToEvent)]);
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<VerificationResult> VerifyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await InTenantAsync(tenantId, (session, _) => SqlVerifier.VerifyAsync(session, cancellationToken), cancellationToken).ConfigureAwait(false);
}
