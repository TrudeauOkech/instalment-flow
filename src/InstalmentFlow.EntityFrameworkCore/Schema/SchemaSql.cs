namespace InstalmentFlow.EntityFrameworkCore.Schema;

/// <summary>
/// The parts of the schema EF Core cannot express (doc 05 §4 and §4a): append-only triggers on facts, the deferred
/// balance check on the journal (I-1), and row-level security on every tenant table (ADR-007). Applied by the
/// initial migration. Roles and grants are not part of migrations (<see cref="InstalmentFlowPostgres.MigrateAsync"/>).
/// </summary>
internal static class SchemaSql
{
    /// <summary>Tables whose rows are facts: inserted once, never updated or deleted (I-2).</summary>
    public static readonly string[] AppendOnlyTables =
    [
        "tenant_policy_versions", "obligation_originated_components", "payments", "payment_assignments", "allocations",
        "allocation_reversals", "payment_reversals", "charges", "charge_reversals", "waivers", "payment_exceptions",
        "payment_exception_resolutions", "journal_transactions", "ledger_entries", "idempotency_keys", "reversal_references",
    ];

    /// <summary>Projection and work tables, which commands update.</summary>
    public static readonly string[] MutableTables =
    [
        "tenants", "accounts", "obligations", "plan_versions", "instalments", "instalment_components", "funds_lots",
        "unidentified_pools", "pending_reversals", "refunds", "outbox",
    ];

    /// <summary>The session setting that scopes every row to one tenant.</summary>
    public const string TenantSetting = "instalment_flow.tenant_id";

    /// <summary>The session setting the outbox dispatcher uses to read every tenant's pending events.</summary>
    public const string DispatcherSetting = "instalment_flow.dispatcher";

    public static string Up => $$"""
        create function {{InstalmentFlowDbContext.Schema}}.forbid_mutation() returns trigger language plpgsql as $$
        begin
          raise exception 'InstalmentFlow: % on %.% is forbidden (append-only, I-2)', tg_op, tg_table_schema, tg_table_name
            using errcode = 'P0001';
        end $$;

        create function {{InstalmentFlowDbContext.Schema}}.check_balanced() returns trigger language plpgsql as $$
        begin
          if exists (select 1 from {{InstalmentFlowDbContext.Schema}}.ledger_entries
                     where tenant_id = new.tenant_id and journal_tx_id = new.journal_tx_id
                     group by currency having sum(debit) <> sum(credit)) then
            raise exception 'InstalmentFlow: journal transaction % is unbalanced (I-1)', new.journal_tx_id using errcode = 'P0001';
          end if;
          return null;
        end $$;

        create constraint trigger t_ledger_balanced after insert on {{InstalmentFlowDbContext.Schema}}.ledger_entries
          deferrable initially deferred for each row execute function {{InstalmentFlowDbContext.Schema}}.check_balanced();

        {{string.Concat(AppendOnlyTables.Select(t => $"""
        create trigger t_{t}_append_only before update or delete on {InstalmentFlowDbContext.Schema}.{t}
          for each row execute function {InstalmentFlowDbContext.Schema}.forbid_mutation();
        create trigger t_{t}_no_truncate before truncate on {InstalmentFlowDbContext.Schema}.{t}
          for each statement execute function {InstalmentFlowDbContext.Schema}.forbid_mutation();

        """))}}
        {{string.Concat(AppendOnlyTables.Concat(MutableTables).Select(t => $"""
        alter table {InstalmentFlowDbContext.Schema}.{t} enable row level security;
        alter table {InstalmentFlowDbContext.Schema}.{t} force row level security;
        create policy tenant_isolation on {InstalmentFlowDbContext.Schema}.{t}
          using (tenant_id = current_setting('{TenantSetting}', true))
          with check (tenant_id = current_setting('{TenantSetting}', true));

        """))}}
        create policy dispatcher on {{InstalmentFlowDbContext.Schema}}.outbox
          using (current_setting('{{DispatcherSetting}}', true) = 'on');
        """;

    public static string Down => $"""
        drop function if exists {InstalmentFlowDbContext.Schema}.check_balanced() cascade;
        drop function if exists {InstalmentFlowDbContext.Schema}.forbid_mutation() cascade;
        """;

    /// <summary>Grants for the application role: read and write projections, append facts, never change them.</summary>
    public static string GrantApplication(string role) => $"""
        grant usage on schema {InstalmentFlowDbContext.Schema} to {Quote(role)};
        grant select, insert on all tables in schema {InstalmentFlowDbContext.Schema} to {Quote(role)};
        grant update on {string.Join(", ", MutableTables.Select(t => $"{InstalmentFlowDbContext.Schema}.{t}"))} to {Quote(role)};
        grant delete on {InstalmentFlowDbContext.Schema}.pending_reversals to {Quote(role)};
        grant usage on all sequences in schema {InstalmentFlowDbContext.Schema} to {Quote(role)};
        """;

    /// <summary>Grants for the outbox dispatcher role: it reads and marks outbox rows of every tenant, and nothing else.</summary>
    public static string GrantDispatcher(string role) => $"""
        grant usage on schema {InstalmentFlowDbContext.Schema} to {Quote(role)};
        grant select, update on {InstalmentFlowDbContext.Schema}.outbox to {Quote(role)};
        """;

    /// <summary>Quotes a role name as an SQL identifier.</summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
