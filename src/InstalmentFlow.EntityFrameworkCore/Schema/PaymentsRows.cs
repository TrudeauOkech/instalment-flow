using Microsoft.EntityFrameworkCore;

namespace InstalmentFlow.EntityFrameworkCore.Schema;

// Payments package tables (doc 05 §3, "Payments package"). Routing, the inbox, the intent check schedule and run
// records are written before an event is routed, so they are not tenant-scoped; intents and discrepancies are, with
// row-level security like every other tenant table.

internal sealed record ProviderAccountRow
{
    public required string Provider { get; set; }

    public required string ProviderAccount { get; set; }

    public required string TenantId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}

internal sealed record InboxRow
{
    public required Guid InboxId { get; set; }

    public required string Provider { get; set; }

    public required string DedupeKey { get; set; }

    public required short Origin { get; set; }

    public required short Kind { get; set; }

    /// <summary>The normalised event as JSON (jsonb).</summary>
    public required string Event { get; set; }

    /// <summary>The raw payload, protected when the host configures an <c>IPayloadProtector</c>.</summary>
    public required byte[] RawPayload { get; set; }

    public required DateTimeOffset ReceivedAt { get; set; }

    public required short Status { get; set; }

    public required int Attempts { get; set; }

    public required DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Error { get; set; }

    public string? TenantId { get; set; }

    public Guid? PaymentId { get; set; }
}

internal sealed record IntentRow
{
    public required string TenantId { get; set; }

    public required Guid IntentId { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string Fingerprint { get; set; }

    public required string Provider { get; set; }

    public required string ProviderAccount { get; set; }

    public required string Payer { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required short TargetKind { get; set; }

    public Guid? ObligationId { get; set; }

    public Guid? AccountId { get; set; }

    public required string AccountReference { get; set; }

    public string? Description { get; set; }

    public required short Status { get; set; }

    public string? CorrelationId { get; set; }

    public string? TransactionId { get; set; }

    public Guid? PaymentId { get; set; }

    public string? FailureReason { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? NextCheckAt { get; set; }

    public required int StatusChecks { get; set; }

    public required long Version { get; set; }
}

/// <summary>When each open intent is next checked: IDs and times only, so the resolver can scan every tenant without reading their data.</summary>
internal sealed record IntentCheckRow
{
    public required Guid IntentId { get; set; }

    public required string TenantId { get; set; }

    public required DateTimeOffset NextCheckAt { get; set; }
}

internal sealed record ReconciliationRunRow
{
    public required Guid RunId { get; set; }

    public required string Provider { get; set; }

    public required string ProviderAccount { get; set; }

    public required DateTimeOffset WindowStart { get; set; }

    public required DateTimeOffset WindowEnd { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public required DateTimeOffset FinishedAt { get; set; }

    public required int Lines { get; set; }

    public required int Matched { get; set; }

    public required int MissingInEngine { get; set; }

    public required int MissingAtProvider { get; set; }

    public required int AmountMismatches { get; set; }

    public required int ReversedAtProvider { get; set; }

    public required int Unroutable { get; set; }
}

internal sealed record DiscrepancyRow
{
    public required string TenantId { get; set; }

    public required Guid DiscrepancyId { get; set; }

    /// <summary>The run that found it; the run's record is written when the run finishes.</summary>
    public required Guid RunId { get; set; }

    public required string Provider { get; set; }

    public required string ProviderAccount { get; set; }

    public required short Kind { get; set; }

    public required string TransactionId { get; set; }

    public decimal? StatementAmount { get; set; }

    public decimal? EngineAmount { get; set; }

    public string? Currency { get; set; }

    public Guid? PaymentId { get; set; }

    public required DateTimeOffset DetectedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? Resolution { get; set; }
}

internal static class PaymentsModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderAccountRow>(e =>
        {
            e.ToTable("tenant_provider_accounts").HasKey(x => new { x.Provider, x.ProviderAccount });
            e.HasOne<TenantRow>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InboxRow>(e =>
        {
            e.ToTable("provider_inbox").HasKey(x => x.InboxId);
            e.HasIndex(x => new { x.Provider, x.DedupeKey }).IsUnique().HasDatabaseName("ux_provider_inbox_dedupe");
            e.HasIndex(x => new { x.NextAttemptAt, x.ReceivedAt }).HasFilter("status = 0").HasDatabaseName("ix_provider_inbox_pending");
            e.Property(x => x.Event).HasColumnType("jsonb");
        });

        modelBuilder.Entity<IntentRow>(e =>
        {
            e.ToTable("payment_intents", t => t.HasCheckConstraint("ck_payment_intents_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.IntentId });
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_payment_intents_key");
            e.HasIndex(x => new { x.TenantId, x.Provider, x.CorrelationId }).IsUnique().HasDatabaseName("ux_payment_intents_correlation");
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasOne<TenantRow>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntentCheckRow>(e =>
        {
            e.ToTable("intent_checks").HasKey(x => x.IntentId);
            e.HasIndex(x => x.NextCheckAt);
        });

        modelBuilder.Entity<ReconciliationRunRow>(e =>
        {
            e.ToTable("reconciliation_runs").HasKey(x => x.RunId);
            e.HasIndex(x => new { x.Provider, x.ProviderAccount, x.WindowEnd });
        });

        modelBuilder.Entity<DiscrepancyRow>(e =>
        {
            e.ToTable("reconciliation_discrepancies").HasKey(x => new { x.TenantId, x.DiscrepancyId });
            e.HasIndex(x => new { x.TenantId, x.Provider, x.ProviderAccount, x.TransactionId, x.Kind }).IsUnique().HasDatabaseName("ux_reconciliation_discrepancies_line");
            e.Property(x => x.StatementAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.EngineAmount).HasColumnType("numeric(19,4)");
            e.HasOne<TenantRow>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Row-level security for the payments tables that belong to one tenant; applied by the payments migration.</summary>
internal static class PaymentsSchemaSql
{
    /// <summary>Payments tables scoped to one tenant.</summary>
    public static readonly string[] TenantTables = ["payment_intents", "reconciliation_discrepancies"];

    /// <summary>Payments tables written before an event is routed to a tenant.</summary>
    public static readonly string[] GlobalTables = ["tenant_provider_accounts", "provider_inbox", "intent_checks", "reconciliation_runs"];

    private static readonly string[] UpdatedTables = ["provider_inbox", "payment_intents", "intent_checks", "reconciliation_discrepancies"];

    public static string Up => string.Concat(TenantTables.Select(t => $"""
        alter table {InstalmentFlowDbContext.Schema}.{t} enable row level security;
        alter table {InstalmentFlowDbContext.Schema}.{t} force row level security;
        create policy tenant_isolation on {InstalmentFlowDbContext.Schema}.{t}
          using (tenant_id = current_setting('{SchemaSql.TenantSetting}', true))
          with check (tenant_id = current_setting('{SchemaSql.TenantSetting}', true));

        """));

    /// <summary>Grants for the application role on the payments tables, beyond select and insert on every table.</summary>
    public static string GrantApplication(string role) => $"""
        grant update on {string.Join(", ", UpdatedTables.Select(t => $"{InstalmentFlowDbContext.Schema}.{t}"))} to {SchemaSql.Quote(role)};
        grant delete on {InstalmentFlowDbContext.Schema}.tenant_provider_accounts, {InstalmentFlowDbContext.Schema}.intent_checks to {SchemaSql.Quote(role)};
        """;
}
