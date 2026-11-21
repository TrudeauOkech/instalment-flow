using InstalmentFlow.Domain;
using InstalmentFlow.EntityFrameworkCore.Schema;
using InstalmentFlow.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

internal sealed partial class PostgresStore
{
    /// <summary>
    /// Applies a commit atomically (doc 05 §5): version checks, projection rows that changed, facts, journal,
    /// idempotency and outbox, in one transaction. Database constraints back every rule; their violations are
    /// mapped to the same exceptions the in-memory store throws.
    /// </summary>
    public async ValueTask CommitAsync(Commit commit, CancellationToken cancellationToken)
    {
        // Invariant I-18: a commit may only touch its own tenant (row-level security would refuse it too).
        if (commit.Accounts.Any(a => a.TenantId != commit.Tenant) || commit.Pools.Any(p => p.TenantId != commit.Tenant) ||
            (commit.Journal is not null && commit.Journal.Tenant != commit.Tenant))
        {
            throw new RuleViolationException(ErrorCodes.TenantMismatch, "A commit touched another tenant's data.");
        }

        try
        {
            await InTenantAsync(commit.Tenant, async (session, db) =>
            {
                await WriteAsync(session, db, commit, cancellationToken).ConfigureAwait(false);
                if (BeforeCommit is { } hook)
                {
                    await hook(session, cancellationToken).ConfigureAwait(false);
                }

                await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Translate(pg, commit) ?? throw new InvalidOperationException($"The commit was refused by the database: {pg.MessageText}", ex);
        }
        catch (PostgresException pg) when (Translate(pg, commit) is { } translated)
        {
            throw translated;
        }
    }

    private static async Task WriteAsync(Session session, InstalmentFlowDbContext db, Commit commit, CancellationToken ct)
    {
        var tenant = commit.Tenant.Value;
        await GuardReversalRacesAsync(session, db, commit, ct).ConfigureAwait(false);

        foreach (var account in commit.Accounts.Where(a => a.Version != CustomerAccount.NewVersion))
        {
            var updated = await db.Accounts.Where(a => a.TenantId == tenant && a.AccountId == account.Id.Value && a.Version == account.Version)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.Version, x => x.Version + 1), ct).ConfigureAwait(false);
            if (updated == 0)
            {
                throw new ConcurrencyConflictException(account.Id, account.Version);
            }
        }

        foreach (var pool in commit.Pools)
        {
            if (pool.Version == CustomerAccount.NewVersion)
            {
                db.Pools.Add(new PoolRow { TenantId = tenant, Source = pool.Source, Version = 1 });
            }
            else if (await db.Pools.Where(p => p.TenantId == tenant && p.Source == pool.Source && p.Version == pool.Version)
                         .ExecuteUpdateAsync(p => p.SetProperty(x => x.Version, x => x.Version + 1), ct).ConfigureAwait(false) == 0)
            {
                throw new ConcurrencyConflictException(default, pool.Version);
            }
        }

        await WriteProjectionsAsync(session, db, commit, ct).ConfigureAwait(false);
        AddFacts(db, commit);

        foreach (var refund in commit.Refunds.Where(r => r.Status != RefundStatus.Requested))
        {
            var row = Mapping.ToRow(tenant, refund);
            var changed = await db.Refunds.Where(r => r.TenantId == tenant && r.RefundId == refund.Id.Value && r.Status == (short)RefundStatus.Requested)
                .ExecuteUpdateAsync(r => r
                    .SetProperty(x => x.Status, row.Status)
                    .SetProperty(x => x.CompletedAt, row.CompletedAt)
                    .SetProperty(x => x.ProviderReferenceSource, row.ProviderReferenceSource)
                    .SetProperty(x => x.ProviderReferenceValue, row.ProviderReferenceValue)
                    .SetProperty(x => x.FailureReason, row.FailureReason), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new RuleViolationException(ErrorCodes.RefundNotPending, $"Refund {refund.Id} is not pending.");
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reversal before payment: parking a reversal and recording its payment must never miss each other. Both take
    /// a transaction-scoped advisory lock on the payment's provider reference, then check the other side.
    /// </summary>
    private static async Task GuardReversalRacesAsync(Session session, InstalmentFlowDbContext db, Commit commit, CancellationToken ct)
    {
        var tenant = commit.Tenant.Value;
        var sources = commit.Payments.Select(p => p.Source).Concat(commit.NewPendingReversals.Select(p => p.PaymentSource)).Distinct()
            .Select(s => $"{tenant}|{s.Source}|{s.Value}").Order(StringComparer.Ordinal).ToList();
        foreach (var key in sources)
        {
            var command = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@k, 0))", session.Connection, session.Transaction);
            await using var commandScope = command.ConfigureAwait(false);
            command.Parameters.AddWithValue("k", key);
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        var applied = commit.AppliedPendingReversals.Select(r => r.Value).ToList();
        if (applied.Count > 0 &&
            await db.PendingReversals.Where(p => p.TenantId == tenant && applied.Contains(p.ReversalId)).ExecuteDeleteAsync(ct).ConfigureAwait(false) != applied.Count)
        {
            throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);   // another command applied it first
        }

        foreach (var pending in commit.NewPendingReversals)
        {
            if (await db.Payments.AnyAsync(p => p.TenantId == tenant && p.Source == pending.PaymentSource.Source && p.ExternalRef == pending.PaymentSource.Value, ct).ConfigureAwait(false))
            {
                throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);
            }
        }

        foreach (var payment in commit.Payments)
        {
            if (await db.PendingReversals.AnyAsync(p => p.TenantId == tenant && p.PaymentSource == payment.Source.Source && p.PaymentRef == payment.Source.Value, ct).ConfigureAwait(false))
            {
                throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);
            }
        }
    }

    /// <summary>Writes the projection rows of each changed aggregate that differ from the version it was loaded at.</summary>
    private static async Task WriteProjectionsAsync(Session session, InstalmentFlowDbContext db, Commit commit, CancellationToken ct)
    {
        var tenant = commit.Tenant.Value;
        var baseLots = new Dictionary<Guid, FundsLotRow>();
        var newLots = new List<FundsLotRow>();

        foreach (var account in commit.Accounts)
        {
            var after = Mapping.ToRows(account with { Version = account.Version + 1 });
            AccountRows? before = null;
            if (account.Version != CustomerAccount.NewVersion)
            {
                var loaded = session.LoadedAccounts.TryGetValue(account.Id, out var cached) && cached.Version == account.Version
                    ? cached
                    : await LoadAccountRowsAsync(db, tenant, account.Id.Value, ct).ConfigureAwait(false) ?? throw new ConcurrencyConflictException(account.Id, account.Version);
                before = Mapping.ToRows(loaded with { Version = account.Version + 1 });
            }
            else
            {
                db.Accounts.Add(after.Account);
            }

            Upsert(db, before?.Obligations, after.Obligations, o => o.ObligationId);
            Upsert(db, before?.Originated, after.Originated, c => (c.ObligationId, c.Component, c.ChargeCode));
            Upsert(db, before?.Plans, after.Plans, p => (p.ObligationId, p.Version));
            Upsert(db, before?.Instalments, after.Instalments, i => (i.ObligationId, i.PlanVersion, i.Sequence));
            Upsert(db, before?.Components, after.Components, c => (c.ObligationId, c.PlanVersion, c.Sequence, c.Component, c.ChargeCode));
            foreach (var lot in before?.Lots ?? [])
            {
                baseLots[lot.PaymentId] = lot;
            }

            newLots.AddRange(after.Lots);
        }

        foreach (var pool in commit.Pools)
        {
            var loaded = session.LoadedPools.TryGetValue(pool.Source, out var cached) && cached.Version == pool.Version
                ? cached
                : await LoadPoolAsync(db, tenant, pool.Source, ct).ConfigureAwait(false);
            foreach (var lot in loaded.Lots)
            {
                baseLots.TryAdd(lot.PaymentId.Value, Mapping.ToRow(tenant, lot, null, pool.Source));
            }

            newLots.AddRange(pool.Lots.Select(l => Mapping.ToRow(tenant, l, null, pool.Source)));
        }

        // A lot can move between holders (unidentified funds assigned to a customer): one row, updated in place.
        foreach (var lot in newLots)
        {
            if (!baseLots.TryGetValue(lot.PaymentId, out var before))
            {
                db.FundsLots.Add(lot);
            }
            else if (before with { Ordinal = 0 } != lot)
            {
                await db.FundsLots.Where(l => l.TenantId == tenant && l.PaymentId == lot.PaymentId).ExecuteUpdateAsync(l => l
                    .SetProperty(x => x.AccountId, lot.AccountId)
                    .SetProperty(x => x.PoolSource, lot.PoolSource)
                    .SetProperty(x => x.Remaining, lot.Remaining)
                    .SetProperty(x => x.Reversed, lot.Reversed)
                    .SetProperty(x => x.Refunded, lot.Refunded)
                    .SetProperty(x => x.Deficit, lot.Deficit)
                    .SetProperty(x => x.Directive, lot.Directive), ct).ConfigureAwait(false);
            }
        }
    }

    private static void Upsert<TRow, TKey>(InstalmentFlowDbContext db, List<TRow>? before, List<TRow> after, Func<TRow, TKey> key)
        where TRow : class
        where TKey : notnull
    {
        var existing = (before ?? []).ToDictionary(key);
        foreach (var row in after)
        {
            if (!existing.TryGetValue(key(row), out var old))
            {
                db.Add(row);
            }
            else if (!EqualityComparer<TRow>.Default.Equals(old, row))
            {
                db.Update(row);
            }
        }
    }

    private static void AddFacts(InstalmentFlowDbContext db, Commit commit)
    {
        var tenant = commit.Tenant.Value;
        var applied = commit.AppliedPendingReversals.ToHashSet();

        if (commit.Journal is { } journal)
        {
            var (transaction, lines) = Mapping.ToRows(journal);
            db.JournalTransactions.Add(transaction);
            db.LedgerEntries.AddRange(lines);
        }

        db.Payments.AddRange(commit.Payments.Select(p => Mapping.ToRow(tenant, p)));
        db.Assignments.AddRange(commit.Assignments.Select(a => Mapping.ToRow(tenant, a)));
        db.Allocations.AddRange(commit.Allocations.Select((a, i) => Mapping.ToRow(tenant, a, i)));
        db.AllocationReversals.AddRange(commit.AllocationReversals.Select((u, i) => Mapping.ToRow(tenant, u, i)));
        db.PaymentReversals.AddRange(commit.PaymentReversals.Select((r, i) => Mapping.ToRow(tenant, r, i)));
        db.PendingReversals.AddRange(commit.NewPendingReversals.Select(p => Mapping.ToRow(tenant, p)));

        // A reversal reference is reserved once, when the reversal is first recorded or parked.
        foreach (var (id, reference) in commit.PaymentReversals.Where(r => !applied.Contains(r.Id)).Select(r => (r.Id, r.Reference))
                     .Concat(commit.NewPendingReversals.Select(p => (p.Id, p.Reference))))
        {
            if (reference is not null)
            {
                db.ReversalReferences.Add(new ReversalReferenceRow { TenantId = tenant, Source = reference.Source, Value = reference.Value, ReversalId = id.Value });
            }
        }

        db.Charges.AddRange(commit.Charges.Select(c => Mapping.ToRow(tenant, c)));
        db.ChargeReversals.AddRange(commit.ChargeReversals.Select(c => Mapping.ToRow(tenant, c)));
        db.Waivers.AddRange(commit.Waivers.Select((w, i) => Mapping.ToRow(tenant, w, i)));
        db.Refunds.AddRange(commit.Refunds.Where(r => r.Status == RefundStatus.Requested).Select(r => Mapping.ToRow(tenant, r)));

        foreach (var exception in commit.NewExceptions)
        {
            db.PaymentExceptions.Add(Mapping.ToRow(tenant, exception));
            if (exception.Resolution is { } resolution)
            {
                db.ExceptionResolutions.Add(Mapping.ToResolutionRow(tenant, exception.Id, resolution));
            }
        }

        foreach (var resolved in commit.ResolvedExceptions)
        {
            if (resolved.Resolution is not { } resolution)
            {
                throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, $"Payment exception {resolved.Id} has no resolution.");
            }

            db.ExceptionResolutions.Add(Mapping.ToResolutionRow(tenant, resolved.Id, resolution));
        }

        if (commit.Idempotency is { } idempotency)
        {
            db.IdempotencyKeys.Add(Mapping.ToRow(tenant, idempotency));
        }

        db.Outbox.AddRange(commit.Events.Select(Mapping.ToRow));
    }

    /// <summary>Maps a database refusal to the exception the engine expects, or null if it indicates a bug.</summary>
    private static Exception? Translate(PostgresException pg, Commit commit)
    {
        switch (pg.SqlState)
        {
            case PostgresErrorCodes.UniqueViolation:
                return pg.ConstraintName switch
                {
                    "pk_accounts" => new ConcurrencyConflictException(commit.Accounts.FirstOrDefault()?.Id ?? default, CustomerAccount.NewVersion),
                    "pk_unidentified_pools" => new ConcurrencyConflictException(default, CustomerAccount.NewVersion),
                    "ux_accounts_external_id" => new RuleViolationException(ErrorCodes.DuplicateExternalId, "Account external ID is already in use in this tenant."),
                    "ux_obligations_external_id" => new RuleViolationException(ErrorCodes.DuplicateExternalId, "Obligation external ID is already in use in this tenant."),
                    "ux_obligations_payment_reference" => new RuleViolationException(ErrorCodes.DuplicatePaymentReference, "Payment reference is already in use in this tenant."),
                    "ux_payments_source" => new DuplicatePaymentException(commit.Payments.First().Source),
                    "pk_idempotency_keys" => new IdempotencyKeyTakenException(commit.Idempotency?.Key ?? string.Empty),
                    "pk_payment_exception_resolutions" => new RuleViolationException(ErrorCodes.ExceptionNotResolvable, "The payment exception is not open."),
                    "pk_payment_assignments" => new RuleViolationException(ErrorCodes.ExceptionNotResolvable, "The payment has already been assigned."),
                    "pk_reversal_references" => new DuplicateReversalException(
                        commit.PaymentReversals.Select(r => r.Reference).Concat(commit.NewPendingReversals.Select(p => p.Reference)).OfType<ExternalReference>().First()),
                    "pk_charge_reversals" => new RuleViolationException(ErrorCodes.InvalidRequest, "The charge is already reversed."),
                    "pk_refunds" => new RuleViolationException(ErrorCodes.RefundNotPending, "The refund already exists."),
                    _ => null,
                };

            case PostgresErrorCodes.ForeignKeyViolation when pg.ConstraintName?.StartsWith("fk_payment_exception_resolutions", StringComparison.Ordinal) == true:
                return new RuleViolationException(ErrorCodes.ExceptionNotResolvable, "The payment exception does not exist.");

            case PostgresErrorCodes.ForeignKeyViolation when pg.ConstraintName?.StartsWith("fk_charge_reversals", StringComparison.Ordinal) == true:
                return new RuleViolationException(ErrorCodes.InvalidRequest, "The charge does not exist.");

            case PostgresErrorCodes.InsufficientPrivilege:
                // Row-level security refused a row outside the session's tenant (ADR-007).
                return new RuleViolationException(ErrorCodes.TenantMismatch, "A commit touched another tenant's data.");

            default:
                return null;
        }
    }
}
