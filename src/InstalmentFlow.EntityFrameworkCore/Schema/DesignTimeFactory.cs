using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InstalmentFlow.EntityFrameworkCore.Schema;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model. Never used at run time; no database is contacted.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InstalmentFlowDbContext>
{
    public InstalmentFlowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InstalmentFlowDbContext>()
            .UseNpgsql("Host=design-time-only", o => o.MigrationsHistoryTable("__ef_migrations_history", InstalmentFlowDbContext.Schema))
            .Options);
}
