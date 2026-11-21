using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InstalmentFlow.EntityFrameworkCore.Schema;

/// <summary>
/// The EF Core model of the InstalmentFlow schema (doc 05). Names are snake_case in the <c>instalment_flow</c> schema;
/// every key and foreign key starts with <c>tenant_id</c>, so a row cannot reference another tenant's row (ADR-007).
/// Triggers, row-level security and grants are added by the migration in SQL (<see cref="SchemaSql"/>).
/// </summary>
internal sealed class InstalmentFlowDbContext(DbContextOptions<InstalmentFlowDbContext> options) : DbContext(options)
{
    public const string Schema = "instalment_flow";

    public DbSet<TenantRow> Tenants => Set<TenantRow>();

    public DbSet<PolicyVersionRow> PolicyVersions => Set<PolicyVersionRow>();

    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    public DbSet<ObligationRow> Obligations => Set<ObligationRow>();

    public DbSet<OriginatedComponentRow> OriginatedComponents => Set<OriginatedComponentRow>();

    public DbSet<PlanVersionRow> PlanVersions => Set<PlanVersionRow>();

    public DbSet<InstalmentRow> Instalments => Set<InstalmentRow>();

    public DbSet<InstalmentComponentRow> InstalmentComponents => Set<InstalmentComponentRow>();

    public DbSet<FundsLotRow> FundsLots => Set<FundsLotRow>();

    public DbSet<PoolRow> Pools => Set<PoolRow>();

    public DbSet<PaymentRow> Payments => Set<PaymentRow>();

    public DbSet<AssignmentRow> Assignments => Set<AssignmentRow>();

    public DbSet<AllocationRow> Allocations => Set<AllocationRow>();

    public DbSet<AllocationReversalRow> AllocationReversals => Set<AllocationReversalRow>();

    public DbSet<PaymentReversalRow> PaymentReversals => Set<PaymentReversalRow>();

    public DbSet<PendingReversalRow> PendingReversals => Set<PendingReversalRow>();

    public DbSet<ReversalReferenceRow> ReversalReferences => Set<ReversalReferenceRow>();

    public DbSet<ChargeRow> Charges => Set<ChargeRow>();

    public DbSet<ChargeReversalRow> ChargeReversals => Set<ChargeReversalRow>();

    public DbSet<WaiverRow> Waivers => Set<WaiverRow>();

    public DbSet<RefundRow> Refunds => Set<RefundRow>();

    public DbSet<PaymentExceptionRow> PaymentExceptions => Set<PaymentExceptionRow>();

    public DbSet<ExceptionResolutionRow> ExceptionResolutions => Set<ExceptionResolutionRow>();

    public DbSet<JournalTransactionRow> JournalTransactions => Set<JournalTransactionRow>();

    public DbSet<LedgerEntryRow> LedgerEntries => Set<LedgerEntryRow>();

    public DbSet<IdempotencyRow> IdempotencyKeys => Set<IdempotencyRow>();

    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    /// <summary>PostgreSQL stores instants as UTC; every offset is normalised on the way in, so hosts may pass any offset.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TenantRow>(e =>
        {
            e.ToTable("tenants").HasKey(x => x.TenantId);
            e.Property(x => x.BaseCurrency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<PolicyVersionRow>(e =>
        {
            e.ToTable("tenant_policy_versions").HasKey(x => new { x.TenantId, x.Version });
            e.Property(x => x.Policy).HasColumnType("jsonb");
            Tenant(e);
        });

        modelBuilder.Entity<AccountRow>(e =>
        {
            e.ToTable("accounts").HasKey(x => new { x.TenantId, x.AccountId });
            e.HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique().HasDatabaseName("ux_accounts_external_id");
            Tenant(e);
        });

        modelBuilder.Entity<ObligationRow>(e =>
        {
            e.ToTable("obligations").HasKey(x => new { x.TenantId, x.ObligationId });
            e.HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique().HasDatabaseName("ux_obligations_external_id");
            e.HasIndex(x => new { x.TenantId, x.PaymentReference }).IsUnique().HasDatabaseName("ux_obligations_payment_reference");
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OriginatedComponentRow>(e =>
        {
            e.ToTable("obligation_originated_components").HasKey(x => new { x.TenantId, x.ObligationId, x.Component, x.ChargeCode });
            Money(e, x => x.Amount);
            e.HasOne<ObligationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlanVersionRow>(e =>
        {
            e.ToTable("plan_versions").HasKey(x => new { x.TenantId, x.ObligationId, x.Version });
            e.HasOne<ObligationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstalmentRow>(e =>
        {
            e.ToTable("instalments").HasKey(x => new { x.TenantId, x.ObligationId, x.PlanVersion, x.Sequence });
            e.HasOne<PlanVersionRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId, x.PlanVersion }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstalmentComponentRow>(e =>
        {
            e.ToTable("instalment_components", t =>
            {
                t.HasCheckConstraint("ck_instalment_components_outstanding", "due - allocated - waived >= 0");   // I-6
                t.HasCheckConstraint("ck_instalment_components_parts", "due >= 0 and allocated >= 0 and waived >= 0 and charged >= 0");
            });
            e.HasKey(x => new { x.TenantId, x.ObligationId, x.PlanVersion, x.Sequence, x.Component, x.ChargeCode });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Due);
            Money(e, x => x.Allocated);
            Money(e, x => x.Waived);
            Money(e, x => x.Charged);
            e.HasOne<InstalmentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId, x.PlanVersion, x.Sequence }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FundsLotRow>(e =>
        {
            e.ToTable("funds_lots", t =>
            {
                t.HasCheckConstraint("ck_funds_lots_parts", "remaining >= 0 and reversed >= 0 and refunded >= 0 and deficit >= 0");   // I-5, I-11
                t.HasCheckConstraint("ck_funds_lots_holder", "(account_id is null) <> (pool_source is null)");
            });
            e.HasKey(x => new { x.TenantId, x.PaymentId });
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.HasIndex(x => new { x.TenantId, x.PoolSource });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Ordinal).UseIdentityAlwaysColumn();
            Money(e, x => x.Original);
            Money(e, x => x.Remaining);
            Money(e, x => x.Reversed);
            Money(e, x => x.Refunded);
            Money(e, x => x.Deficit);
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PoolRow>(e =>
        {
            e.ToTable("unidentified_pools").HasKey(x => new { x.TenantId, x.Source });
            Tenant(e);
        });

        modelBuilder.Entity<PaymentRow>(e =>
        {
            e.ToTable("payments", t => t.HasCheckConstraint("ck_payments_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.PaymentId });
            e.HasIndex(x => new { x.TenantId, x.Source, x.ExternalRef }).IsUnique().HasDatabaseName("ux_payments_source");   // I-3
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            Money(e, x => x.Amount);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<AssignmentRow>(e =>
        {
            e.ToTable("payment_assignments").HasKey(x => new { x.TenantId, x.PaymentId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<AllocationRow>(e =>
        {
            e.ToTable("allocations", t => t.HasCheckConstraint("ck_allocations_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.AllocationId });
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.HasIndex(x => new { x.TenantId, x.PaymentId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ObligationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<AllocationReversalRow>(e =>
        {
            e.ToTable("allocation_reversals", t => t.HasCheckConstraint("ck_allocation_reversals_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            e.HasOne<AllocationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.AllocationId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<PaymentReversalRow>(e =>
        {
            e.ToTable("payment_reversals", t => t.HasCheckConstraint("ck_payment_reversals_amount", "amount > 0 and amount = from_funds + unwound + deficit"));
            e.HasKey(x => new { x.TenantId, x.ReversalId });
            e.HasIndex(x => new { x.TenantId, x.PaymentId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            Money(e, x => x.FromFunds);
            Money(e, x => x.Unwound);
            Money(e, x => x.Deficit);
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<PendingReversalRow>(e =>
        {
            e.ToTable("pending_reversals").HasKey(x => new { x.TenantId, x.ReversalId });
            e.HasIndex(x => new { x.TenantId, x.PaymentSource, x.PaymentRef });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            Tenant(e);
        });

        modelBuilder.Entity<ReversalReferenceRow>(e =>
        {
            e.ToTable("reversal_references").HasKey(x => new { x.TenantId, x.Source, x.Value });
            Tenant(e);
        });

        modelBuilder.Entity<ChargeRow>(e =>
        {
            e.ToTable("charges", t => t.HasCheckConstraint("ck_charges_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.ChargeId });
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            e.HasOne<ObligationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<ChargeReversalRow>(e =>
        {
            e.ToTable("charge_reversals").HasKey(x => new { x.TenantId, x.ChargeId });
            e.HasOne<ChargeRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ChargeId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<WaiverRow>(e =>
        {
            e.ToTable("waivers", t => t.HasCheckConstraint("ck_waivers_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.WaiverId, x.Line });
            e.HasIndex(x => new { x.TenantId, x.AccountId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Amount);
            e.HasOne<ObligationRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ObligationId }).OnDelete(DeleteBehavior.Restrict);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<RefundRow>(e =>
        {
            e.ToTable("refunds", t => t.HasCheckConstraint("ck_refunds_amount", "amount > 0"));
            e.HasKey(x => new { x.TenantId, x.RefundId });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.Portions).HasColumnType("jsonb");
            Money(e, x => x.Amount);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).OnDelete(DeleteBehavior.Restrict);
            Tenant(e);
        });

        modelBuilder.Entity<PaymentExceptionRow>(e =>
        {
            e.ToTable("payment_exceptions").HasKey(x => new { x.TenantId, x.ExceptionId });
            e.HasIndex(x => new { x.TenantId, x.PaymentId });
            e.HasIndex(x => new { x.TenantId, x.OpenedAt });
            e.Property(x => x.Candidates).HasColumnType("jsonb");
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExceptionResolutionRow>(e =>
        {
            e.ToTable("payment_exception_resolutions").HasKey(x => new { x.TenantId, x.ExceptionId });
            e.HasOne<PaymentExceptionRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ExceptionId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalTransactionRow>(e =>
        {
            e.ToTable("journal_transactions").HasKey(x => new { x.TenantId, x.JournalTxId });
            e.Property(x => x.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Seq).IsUnique().HasDatabaseName("ux_journal_seq");
            e.HasIndex(x => new { x.TenantId, x.AccountId, x.Seq });
            Tenant(e);
        });

        modelBuilder.Entity<LedgerEntryRow>(e =>
        {
            e.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_ledger_entries_one_sided", "(debit > 0 and credit = 0) or (credit > 0 and debit = 0)"));   // I-1
            e.HasKey(x => new { x.TenantId, x.JournalTxId, x.Line });
            e.HasIndex(x => new { x.TenantId, x.LedgerAccount });
            e.Property(x => x.Currency).HasColumnType("char(3)");
            Money(e, x => x.Debit);
            Money(e, x => x.Credit);
            Journal(e, x => new { x.TenantId, x.JournalTxId });
        });

        modelBuilder.Entity<IdempotencyRow>(e =>
        {
            e.ToTable("idempotency_keys").HasKey(x => new { x.TenantId, x.Key });
            e.Property(x => x.Result).HasColumnType("jsonb");
            Tenant(e);
        });

        modelBuilder.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox").HasKey(x => x.OutboxId);
            e.Property(x => x.OutboxId).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("status = 0").HasDatabaseName("ix_outbox_pending");
            e.HasIndex(x => new { x.TenantId, x.AccountId, x.OutboxId });
            e.Property(x => x.Payload).HasColumnType("jsonb");
            Tenant(e);
        });

        // Snake-case names for every table column, key, index and foreign key.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }

            entity.FindPrimaryKey()!.SetName($"pk_{table}");
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principal = foreignKey.PrincipalEntityType.GetTableName();
                foreignKey.SetConstraintName($"fk_{table}_{principal}_{string.Join('_', foreignKey.Properties.Skip(1).Select(p => SnakeCase(p.Name)))}".TrimEnd('_'));
            }

            foreach (var index in entity.GetIndexes().Where(i => i.GetDatabaseName() is { } name && name.StartsWith("IX_", StringComparison.Ordinal)))
            {
                index.SetDatabaseName($"ix_{table}_{string.Join('_', index.Properties.Select(p => SnakeCase(p.Name)))}");
            }
        }
    }

    private static void Tenant<T>(EntityTypeBuilder<T> e)
        where T : class =>
        e.HasOne<TenantRow>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);

    private static void Journal<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, object?>> key)
        where T : class =>
        e.HasOne<JournalTransactionRow>().WithMany().HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);

    private static void Money<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, decimal>> amount)
        where T : class =>
        e.Property(amount).HasColumnType("numeric(19,4)");

    internal static string SnakeCase(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    text.Append('_');
                }

                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
}
