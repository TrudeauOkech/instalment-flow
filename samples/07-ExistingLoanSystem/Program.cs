// Sample 07: InstalmentFlow inside an existing loan system. The lender's own application has its own EF Core
// DbContext and tables; when it disburses a loan it writes its application record and opens the repayment schedule
// in InstalmentFlow in ONE database transaction (InstalmentFlowPostgres.Enlist), so either both happen or neither.
//
// Needs PostgreSQL: set INSTALMENTFLOW_SAMPLE_PG to a connection string. Without it, it explains and exits.
// For brevity it migrates and runs with that one role; in production, migrate as the schema owner and run as an
// application role without BYPASSRLS (docs/security/threat-model.md).
using InstalmentFlow;
using InstalmentFlow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

var connection = Environment.GetEnvironmentVariable("INSTALMENTFLOW_SAMPLE_PG");
if (string.IsNullOrEmpty(connection))
{
    Console.WriteLine("Sample 07 needs PostgreSQL: set INSTALMENTFLOW_SAMPLE_PG, for example");
    Console.WriteLine("  docker run -d -e POSTGRES_PASSWORD=sample -p 5433:5432 postgres:17-alpine");
    Console.WriteLine("  export INSTALMENTFLOW_SAMPLE_PG='Host=localhost;Port=5433;Username=postgres;Password=sample'");
    Console.WriteLine("Skipping: no database.");
    return 0;
}

await InstalmentFlowPostgres.MigrateAsync(connection);   // explicit, never silent
var factory = await InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = connection },
    b => b.AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"));
var flow = factory.ForTenant("acme-ke");

await using (var setup = Lms.Create(connection))
{
    await setup.Database.ExecuteSqlRawAsync("""
        create schema if not exists lms;
        create table if not exists lms.loan_applications (
          application_id uuid primary key, customer text not null, amount numeric(19,2) not null, status text not null);
        """);
}

var run = Guid.NewGuid().ToString("N")[..8];
var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{run}", ExternalId = $"CUST-{run}" });

// 1. Disburse: the lender's record and the repayment schedule commit together.
await DisburseAsync($"LN{run}A", 3_000m, commit: true);

// 2. A disbursement that fails after the schedule was opened (here: the lender's own check rejects it) leaves nothing.
await DisburseAsync($"LN{run}B", 9_000m, commit: false);

var kept = await flow.Queries.GetObligationAsync(ObligationRef.ByReference($"LN{run}A"));
var rolledBack = await Exists($"LN{run}B");
await using var lms = Lms.Create(connection);
var applications = await lms.Applications.Where(a => a.Customer == $"CUST-{run}").Select(a => a.Status).ToListAsync();
var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant);

Console.WriteLine($"Loan A: outstanding {kept.Outstanding}; loan B exists: {rolledBack}; lender records: {string.Join(", ", applications)}");
var ok = kept.Outstanding == Money.Kes(3_000m) && !rolledBack && applications is ["Disbursed"] && report.Passed;
Console.WriteLine(ok ? "OK: the lender's records and InstalmentFlow committed and rolled back together." : "FAILED");
return ok ? 0 : 1;

async Task DisburseAsync(string reference, decimal amount, bool commit)
{
    await using var lms = Lms.Create(connection);
    await using var transaction = await lms.Database.BeginTransactionAsync();
    lms.Applications.Add(new LoanApplication { ApplicationId = Guid.NewGuid(), Customer = $"CUST-{run}", Amount = amount, Status = "Disbursed" });
    await lms.SaveChangesAsync();

    using (InstalmentFlowPostgres.Enlist(transaction.GetDbTransaction()))   // InstalmentFlow joins the lender's transaction
    {
        await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = $"open-{reference}", Account = customer.AccountId, ExternalId = reference, PaymentReference = reference,
            Components = [Component.Principal(Money.Kes(amount))], Plan = PlanSpec.Equal(Frequency.Monthly, 3, DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)),
        });
    }

    if (commit)
    {
        await transaction.CommitAsync();
    }
    else
    {
        await transaction.RollbackAsync();
    }
}

async Task<bool> Exists(string reference)
{
    try
    {
        await flow.Queries.GetObligationAsync(ObligationRef.ByReference(reference));
        return true;
    }
    catch (NotFoundException)
    {
        return false;
    }
}

/// <summary>The lender's own data: here one table of loan applications.</summary>
internal sealed class Lms(DbContextOptions<Lms> options) : DbContext(options)
{
    public DbSet<LoanApplication> Applications => Set<LoanApplication>();

    public static Lms Create(string connection) => new(new DbContextOptionsBuilder<Lms>().UseNpgsql(connection).Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<LoanApplication>(e =>
        {
            e.ToTable("loan_applications", "lms").HasKey(a => a.ApplicationId);
            e.Property(a => a.ApplicationId).HasColumnName("application_id");
            e.Property(a => a.Customer).HasColumnName("customer");
            e.Property(a => a.Amount).HasColumnName("amount");
            e.Property(a => a.Status).HasColumnName("status");
        });
}

internal sealed class LoanApplication
{
    public Guid ApplicationId { get; set; }

    public required string Customer { get; set; }

    public decimal Amount { get; set; }

    public required string Status { get; set; }
}
