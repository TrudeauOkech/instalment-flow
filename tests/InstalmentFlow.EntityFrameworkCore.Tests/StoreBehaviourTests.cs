using System.Collections.Concurrent;
using InstalmentFlow.EntityFrameworkCore.Persistence;
using InstalmentFlow.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Tests;

/// <summary>The PostgreSQL store under concurrency, failure, restarts and host transactions (M7).</summary>
public sealed class StoreBehaviourTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fifty_parallel_tasks_of_receipts_and_reversals_on_five_accounts_leave_zero_violations()
    {
        var (_, factory) = await Seeded.CreateAsync(retries: 100);   // ten writers per account: an unusually hot account
        var flow = factory.ForTenant(Seeded.Tenant);
        for (var a = 0; a < 5; a++)
        {
            var account = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"a{a}", ExternalId = $"P-{a}" }, Ct);
            await flow.Obligations.OpenAsync(new OpenObligationRequest
            {
                IdempotencyKey = $"o{a}", Account = account.AccountId, ExternalId = $"PL-{a}", PaymentReference = $"PAR-{a}",
                Components = [Component.Principal(Money.Kes(50_000m))], Plan = PlanSpec.Equal(Frequency.Daily, 100, new DateOnly(2026, 1, 1)),
            }, Ct);
        }

        await Parallel.ForAsync(0, 50, new ParallelOptions { MaxDegreeOfParallelism = 50, CancellationToken = Ct }, async (task, ct) =>
        {
            var random = new Random(task);
            for (var i = 0; i < 6; i++)
            {
                var transId = $"T{task}-{i}";
                await flow.Payments.RecordAsync(Seeded.Payment(transId, random.Next(1, 900), $"PAR-{random.Next(5)}"), ct);
                if (random.Next(3) == 0)
                {
                    await flow.Payments.ReverseAsync(new ReversePaymentRequest
                    {
                        IdempotencyKey = $"r-{transId}", Payment = PaymentRef.BySource(new ExternalReference("mpesa:600123", transId)),
                        Amount = random.Next(2) == 0 ? null : Money.Kes(1m), Reason = "random",
                    }, ct);
                }
            }
        });

        var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct);
        Assert.True(report.Passed, string.Join('\n', report.Violations.Select(v => $"{v.Invariant}: {v.Message}")));
        Assert.Equal(301, report.Payments);
        AssertSnapshotVerifies(factory);
    }

    [Fact]
    public async Task A_connection_killed_after_the_payment_insert_but_before_commit_leaves_exactly_one_payment_after_retry()
    {
        var (db, factory) = await Seeded.CreateAsync();
        var flow = factory.ForTenant(Seeded.Tenant);
        var store = (PostgresStore)factory.LedgerStore;
        var killed = 0;
        store.BeforeCommit = async (session, ct) =>
        {
            if (Interlocked.Exchange(ref killed, 1) == 0)
            {
                await Sql.ScalarAsync<bool>(db.Superuser, $"select pg_terminate_backend({session.Connection.ProcessID})");
            }
        };

        var request = Seeded.Payment("TXK", 300m, "LN-1");
        await Assert.ThrowsAnyAsync<Exception>(() => flow.Payments.RecordAsync(request, Ct));
        var retried = await flow.Payments.RecordAsync(request, Ct);

        Assert.NotEqual(RecordOutcome.Duplicate, retried.Outcome);   // the first attempt left nothing behind
        Assert.Equal(1L, await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Tenant, "select count(*) from instalment_flow.payments where external_ref = 'TXK'"));
        Assert.Equal(RecordOutcome.Duplicate, (await flow.Payments.RecordAsync(request, Ct)).Outcome);
        AssertSnapshotVerifies(factory);
    }

    [Fact]
    public async Task Results_replay_after_a_restart()
    {
        var (db, factory) = await Seeded.CreateAsync();
        var request = Seeded.Payment("TXR", 200m, "LN-1");
        var first = await factory.ForTenant(Seeded.Tenant).Payments.RecordAsync(request, Ct);

        var restarted = await InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = db.Application }, cancellationToken: Ct);
        var replay = await restarted.ForTenant(Seeded.Tenant).Payments.RecordAsync(request, Ct);

        Assert.Equal((RecordOutcome.Duplicate, first.PaymentId, first.Allocated), (replay.Outcome, replay.PaymentId, replay.Allocated));
        Assert.Equal(first.Lines.Select(l => (l.InstalmentSequence, l.Amount, l.Strategy)), replay.Lines.Select(l => (l.InstalmentSequence, l.Amount, l.Strategy)));
    }

    [Fact]
    public async Task Another_tenants_ids_are_not_found()
    {
        var (_, factory) = await Seeded.CreateAsync();
        var acme = factory.ForTenant(Seeded.Tenant);
        var beta = factory.ForTenant(Seeded.Other);
        var account = (await acme.Queries.GetAccountAsync(AccountRef.ByExternalId("C-1"), cancellationToken: Ct)).AccountId;
        var loan = (await acme.Queries.GetObligationAsync(ObligationRef.ByReference("LN-1"), cancellationToken: Ct)).ObligationId;
        var payment = PaymentRef.BySource(new ExternalReference("mpesa:600123", "TX1"));

        await Assert.ThrowsAsync<NotFoundException>(() => beta.Queries.GetAccountAsync(account, cancellationToken: Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => beta.Queries.GetObligationAsync(loan, cancellationToken: Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => beta.Queries.GetPaymentAsync(payment, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => beta.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "x", Obligation = loan, Type = ChargeType.Fee, Amount = Money.Of(1m, "UGX"), Description = "x",
        }, Ct));
        Assert.Equal(ReversalOutcome.Parked, (await beta.Payments.ReverseAsync(new ReversePaymentRequest { IdempotencyKey = "r", Payment = payment, Reason = "x" }, Ct)).Outcome);
        Assert.Empty(await beta.Queries.ReadJournalAsync(JournalCursor.Start, Ct).ToListAsync(Ct));
        Assert.True((await acme.Queries.VerifyAsync(VerifyScope.Tenant, Ct)).Passed);
    }

    [Fact]
    public async Task A_command_joins_a_host_owned_transaction_and_is_undone_by_its_rollback()
    {
        var (db, factory) = await Seeded.CreateAsync();
        var flow = factory.ForTenant(Seeded.Tenant);
        var customer = (await flow.Queries.GetAccountAsync(AccountRef.ByExternalId("C-1"), cancellationToken: Ct)).AccountId;

        async Task OpenInHostTransactionAsync(string reference, bool commit)
        {
            await using var connection = new NpgsqlConnection(db.Application);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            using (InstalmentFlowPostgres.Enlist(transaction))
            {
                await flow.Obligations.OpenAsync(new OpenObligationRequest
                {
                    IdempotencyKey = $"host-{reference}", Account = customer, ExternalId = $"H-{reference}", PaymentReference = reference,
                    Components = [Component.Principal(Money.Kes(100m))], Plan = PlanSpec.Equal(Frequency.Monthly, 1, new DateOnly(2027, 1, 1)),
                }, Ct);
                Assert.Equal(reference, (await flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference), cancellationToken: Ct)).PaymentReference);   // visible inside
            }

            if (commit)
            {
                await transaction.CommitAsync(Ct);
            }
            else
            {
                await transaction.RollbackAsync(Ct);
            }
        }

        await OpenInHostTransactionAsync("HOST-1", commit: false);
        await OpenInHostTransactionAsync("HOST-2", commit: true);

        await Assert.ThrowsAsync<NotFoundException>(() => flow.Queries.GetObligationAsync(ObligationRef.ByReference("HOST-1"), cancellationToken: Ct));
        Assert.Equal(ObligationStatus.Active, (await flow.Queries.GetObligationAsync(ObligationRef.ByReference("HOST-2"), cancellationToken: Ct)).Status);
        Assert.True((await flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct)).Passed);
    }

    [Fact]
    public async Task Pessimistic_mode_serialises_concurrent_commands_on_one_account()
    {
        var (_, factory) = await Seeded.CreateAsync(ConcurrencyMode.PessimisticAccountLock);
        var flow = factory.ForTenant(Seeded.Tenant);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => flow.Payments.RecordAsync(Seeded.Payment($"TP{i}", 25m, "LN-1"), Ct), Ct)));

        Assert.All(results, r => Assert.NotEqual(RecordOutcome.Duplicate, r.Outcome));
        Assert.Equal(Money.Kes(1_000m), (await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN-1"), cancellationToken: Ct)).Components.Single().Paid);
        Assert.True((await flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct)).Passed);
    }

    // ----- Outbox -----

    [Fact]
    public async Task Events_committed_before_a_crash_are_still_delivered_at_least_once()
    {
        var (db, factory) = await Seeded.CreateAsync();
        var time = new ManualTime(DateTimeOffset.UtcNow.AddMinutes(1));
        var publisher = new Recording();

        // A dispatcher that crashes after publishing, before marking: the lease runs out and another delivers again.
        var crashing = new OutboxDispatcher(db.Dispatcher, new Recording(crashAfterPublish: true, into: publisher), new OutboxDispatcherOptions { Lease = TimeSpan.FromSeconds(30) }, time);
        await Assert.ThrowsAnyAsync<Exception>(() => crashing.DispatchAsync(Ct));
        var firstDelivery = publisher.Events.Count;

        time.Now += TimeSpan.FromMinutes(1);
        await using var dispatcher = new OutboxDispatcher(db.Dispatcher, publisher, timeProvider: time);
        while (await dispatcher.DispatchAsync(Ct) > 0)
        {
        }

        var committed = await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Tenant, "select count(*) from instalment_flow.outbox");
        Assert.Equal(1, firstDelivery);
        Assert.Equal(committed, publisher.Events.Select(e => e.EventId).Distinct().LongCount());   // every event delivered
        Assert.True(publisher.Events.Count > committed - 1);                                        // and the crashed one twice
        Assert.Equal(0L, await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Tenant, "select count(*) from instalment_flow.outbox where status = 0"));
        await crashing.DisposeAsync();
    }

    [Fact]
    public async Task Events_of_one_account_are_published_in_commit_order_despite_failures_and_competing_dispatchers()
    {
        var (db, factory) = await Seeded.CreateAsync();
        var flow = factory.ForTenant(Seeded.Tenant);
        for (var i = 0; i < 15; i++)
        {
            await flow.Payments.RecordAsync(Seeded.Payment($"TO{i}", 10m, "LN-1"), Ct);
        }

        var time = new ManualTime(DateTimeOffset.UtcNow.AddMinutes(1));
        var publisher = new Recording(failEvery: 4);
        var dispatchers = Enumerable.Range(0, 3).Select(_ => new OutboxDispatcher(db.Dispatcher, publisher, new OutboxDispatcherOptions { BatchSize = 3 }, time)).ToList();
        for (var round = 0; round < 200 && await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Tenant, "select count(*) from instalment_flow.outbox where status = 0") > 0; round++)
        {
            await Task.WhenAll(dispatchers.Select(d => d.DispatchAsync(Ct)));
            time.Now += TimeSpan.FromMinutes(10);   // past every backoff
        }

        var commitOrder = (await factory.LedgerStore.SnapshotAsync(Seeded.Tenant, Ct)).Events.Where(e => e.AccountId is not null).Select(e => e.EventId).ToList();
        var delivered = publisher.Delivered.Where(e => e.AccountId is not null).Select(e => e.EventId).Distinct().ToList();
        Assert.Equal(commitOrder, delivered);
        foreach (var dispatcher in dispatchers)
        {
            await dispatcher.DisposeAsync();
        }
    }

    // ----- Schema -----

    [Fact]
    public async Task Migrations_apply_and_revert_on_an_empty_database_and_the_factory_refuses_a_missing_schema()
    {
        var db = await PostgresServer.CreateEmptyDatabaseAsync();
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = db.Owner }, cancellationToken: Ct));
        Assert.Contains("MigrateAsync", missing.Message, StringComparison.Ordinal);

        await InstalmentFlowPostgres.MigrateAsync(db.Owner, cancellationToken: Ct);
        await InstalmentFlowPostgres.MigrateAsync(db.Owner, cancellationToken: Ct);   // idempotent
        Assert.True(await Sql.ScalarAsync<long>(db.Owner, "select count(*) from pg_tables where schemaname = 'instalment_flow'") >= 27);

        await using (var owner = InstalmentFlowPostgres.Owner(db.Owner))
        {
            await owner.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase, cancellationToken: Ct);
        }

        Assert.Equal(1L, await Sql.ScalarAsync<long>(db.Owner, "select count(*) from pg_tables where schemaname = 'instalment_flow'"));   // only the history table
        Assert.Equal(0L, await Sql.ScalarAsync<long>(db.Owner, "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'instalment_flow'"));

        await InstalmentFlowPostgres.MigrateAsync(db.Owner, cancellationToken: Ct);
        Assert.True(await Sql.ScalarAsync<long>(db.Owner, "select count(*) from pg_tables where schemaname = 'instalment_flow'") >= 27);
    }

    [Fact]
    public void The_migration_script_is_idempotent_and_includes_row_level_security()
    {
        var script = InstalmentFlowPostgres.GenerateMigrationScript();

        Assert.Contains("force row level security", script, StringComparison.Ordinal);
        Assert.Contains("__ef_migrations_history", script, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_factory_refuses_a_schema_newer_than_it_knows()
    {
        var db = await PostgresServer.CreateDatabaseAsync();
        await Sql.ExecuteAsTenantAsync(db.Owner, "none", """insert into instalment_flow.__ef_migrations_history ("MigrationId", "ProductVersion") values ('99990101000000_FromTheFuture', '99.0')""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = db.Application }, cancellationToken: Ct));

        Assert.Contains("FromTheFuture", error.Message, StringComparison.Ordinal);
    }

    private static void AssertSnapshotVerifies(InstalmentFlowFactory factory)
    {
        var violations = Verification.LedgerVerifier.Verify(factory.LedgerStore.SnapshotAsync(Seeded.Tenant, Ct).AsTask().GetAwaiter().GetResult());
        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records every publish attempt; can fail every Nth call or crash after the first publish.</summary>
    private sealed class Recording(int failEvery = 0, bool crashAfterPublish = false, Recording? into = null) : IIntegrationEventPublisher
    {
        private int _calls;

        public ConcurrentQueue<IntegrationEvent> Delivered { get; } = new();

        public List<IntegrationEvent> Events => [.. (into ?? this).Delivered];

        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            if (failEvery > 0 && Interlocked.Increment(ref _calls) % failEvery == 0)
            {
                throw new InvalidOperationException("broker unavailable");
            }

            (into ?? this).Delivered.Enqueue(integrationEvent);
            if (crashAfterPublish)
            {
                throw new OperationCanceledException("process crashed");   // not caught by the dispatcher: it never marks the event
            }

            return Task.CompletedTask;
        }
    }
}
