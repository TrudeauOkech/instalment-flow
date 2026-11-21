using InstalmentFlow.TestSupport;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Tests;

/// <summary>
/// The database enforces the rules on its own (doc 05 §4, §4a): append-only facts, balanced journal transactions,
/// tenant isolation that fails closed, and composite keys that cannot cross tenants. These run raw SQL, as the
/// application role a bug or a manual query would use.
/// </summary>
public sealed class DatabaseEnforcementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Updating_or_deleting_a_ledger_entry_fails_even_for_the_schema_owner()
    {
        var (db, _) = await Seeded.CreateAsync();

        // The application role has no UPDATE or DELETE grant on facts at all…
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            Sql.ExecuteAsTenantAsync(db.Application, Seeded.Tenant, "update instalment_flow.ledger_entries set debit = debit + 1 where debit > 0"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        // …and even the schema owner, who has every privilege, is stopped by the append-only triggers (I-2).
        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            Sql.ExecuteAsTenantAsync(db.Owner, Seeded.Tenant, "update instalment_flow.ledger_entries set debit = debit + 1 where debit > 0"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            Sql.ExecuteAsTenantAsync(db.Owner, Seeded.Tenant, "delete from instalment_flow.payments"));
        Assert.Contains("append-only", update.MessageText, StringComparison.Ordinal);
        Assert.Contains("append-only", delete.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unbalanced_journal_transaction_fails_at_commit()
    {
        var (db, _) = await Seeded.CreateAsync();
        var tx = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsTenantAsync(db.Application, Seeded.Tenant, $"""
            insert into instalment_flow.journal_transactions (tenant_id, journal_tx_id, kind, idempotency_key, effective_date, recorded_at)
              values ('{Seeded.Tenant}', '{tx}', 5, 'tamper', current_date, now());
            insert into instalment_flow.ledger_entries (tenant_id, journal_tx_id, line, ledger_account, currency, debit, credit, source_type, source_id)
              values ('{Seeded.Tenant}', '{tx}', 0, 'x', 'KES', 50, 0, 4, gen_random_uuid()),
                     ('{Seeded.Tenant}', '{tx}', 1, 'y', 'KES', 0, 40, 4, gen_random_uuid());
            """));

        Assert.Contains("unbalanced", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_tenant_setting_every_table_reads_as_empty()
    {
        var (db, _) = await Seeded.CreateAsync();

        foreach (var table in Schema.SchemaSql.AppendOnlyTables.Concat(Schema.SchemaSql.MutableTables).Concat(Schema.PaymentsSchemaSql.TenantTables))
        {
            Assert.Equal(0L, await Sql.ScalarAsync<long>(db.Application, $"select count(*) from instalment_flow.{table}"));
        }

        Assert.True(await Sql.ScalarAsync<long>(db.Superuser, "select count(*) from instalment_flow.payments") > 0);   // the data is there
    }

    [Fact]
    public async Task The_application_role_sees_only_its_tenant_and_cannot_write_into_another()
    {
        var (db, _) = await Seeded.CreateAsync();

        Assert.True(await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Tenant, "select count(*) from instalment_flow.payments") > 0);
        Assert.Equal(0L, await Sql.ScalarAsTenantAsync<long>(db.Application, Seeded.Other, "select count(*) from instalment_flow.payments"));

        var write = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsTenantAsync(db.Application, Seeded.Other,
            $"insert into instalment_flow.pending_reversals (tenant_id, reversal_id, payment_source, payment_ref, reason, at) values ('{Seeded.Tenant}', gen_random_uuid(), 's', 'r', 'x', now())"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, write.SqlState);   // row-level security WITH CHECK
    }

    [Fact]
    public async Task A_foreign_key_into_another_tenant_fails()
    {
        var (db, _) = await Seeded.CreateAsync();
        var otherTenantsTx = await Sql.ScalarAsTenantAsync<Guid>(db.Application, Seeded.Tenant, "select journal_tx_id from instalment_flow.journal_transactions limit 1");

        // Tenant B tries to add a line to tenant A's journal transaction: the composite key (tenant_id, journal_tx_id) has no match.
        var error = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsTenantAsync(db.Application, Seeded.Other, $"""
            insert into instalment_flow.ledger_entries (tenant_id, journal_tx_id, line, ledger_account, currency, debit, credit, source_type, source_id)
              values ('{Seeded.Other}', '{otherTenantsTx}', 9, 'x', 'KES', 1, 0, 4, gen_random_uuid());
            """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task The_application_role_cannot_bypass_row_level_security_or_truncate()
    {
        var (db, _) = await Seeded.CreateAsync();

        Assert.False(await Sql.ScalarAsync<bool>(db.Application, "select rolbypassrls or rolsuper from pg_roles where rolname = current_user"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsTenantAsync(db.Application, Seeded.Tenant, "truncate instalment_flow.ledger_entries"));
    }
}

/// <summary>A migrated database with two tenants; the first has customers, a loan, payments and a reversal.</summary>
internal static class Seeded
{
    public const string Tenant = "acme-ke";
    public const string Other = "beta-ug";

    public static async Task<(PostgresDatabase Database, InstalmentFlowFactory Factory)> CreateAsync(ConcurrencyMode mode = ConcurrencyMode.Optimistic, int retries = 10)
    {
        var db = await PostgresServer.CreateDatabaseAsync();
        var factory = await InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = db.Application, ConcurrencyMode = mode }, b => b
            .UseConcurrencyRetries(retries)
            .AddTenant(Tenant, Currency.KES, "Africa/Nairobi")
            .AddTenant(Other, Currency.UGX, "Africa/Kampala"));
        var flow = factory.ForTenant(Tenant);
        var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" });
        await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN-1",
            Components = [Component.Principal(Money.Kes(1_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, new DateOnly(2026, 1, 1)),
        });
        await flow.Payments.RecordAsync(Payment("TX1", 700m, "LN-1"));
        await flow.Payments.ReverseAsync(new ReversePaymentRequest { IdempotencyKey = "rev", Payment = PaymentRef.BySource(new ExternalReference("mpesa:600123", "TX1")), Amount = Money.Kes(100m), Reason = "test" });
        return (db, factory);
    }

    public static RecordPaymentRequest Payment(string transId, decimal amount, string reference) => new()
    {
        IdempotencyKey = $"pay-{transId}",
        Source = new ExternalReference("mpesa:600123", transId),
        Amount = Money.Kes(amount),
        ValueAt = DateTimeOffset.UtcNow,
        Target = PaymentTarget.ForReference(reference),
    };
}

/// <summary>Raw SQL helpers, optionally inside a transaction scoped to a tenant (as the store does).</summary>
internal static class Sql
{
    public static Task ExecuteAsTenantAsync(string connectionString, string tenant, string sql) =>
        RunAsync<object?>(connectionString, tenant, sql, async c =>
        {
            await c.ExecuteNonQueryAsync().ConfigureAwait(false);
            return null;
        });

    public static Task<T> ScalarAsTenantAsync<T>(string connectionString, string tenant, string sql) =>
        RunAsync(connectionString, tenant, sql, async c => (T)(await c.ExecuteScalarAsync().ConfigureAwait(false))!);

    public static Task<T> ScalarAsync<T>(string connectionString, string sql) =>
        RunAsync(connectionString, null, sql, async c => (T)(await c.ExecuteScalarAsync().ConfigureAwait(false))!);

    private static async Task<T> RunAsync<T>(string connectionString, string? tenant, string sql, Func<NpgsqlCommand, Task<T>> run)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (tenant is not null)
                {
                    var set = new NpgsqlCommand("select set_config('instalment_flow.tenant_id', @t, true)", connection, transaction);
                    await using (set.ConfigureAwait(false))
                    {
                        set.Parameters.AddWithValue("t", tenant);
                        await set.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }

                var command = new NpgsqlCommand(sql, connection, transaction);
                await using (command.ConfigureAwait(false))
                {
                    var result = await run(command).ConfigureAwait(false);
                    await transaction.CommitAsync().ConfigureAwait(false);
                    return result;
                }
            }
        }
    }
}
