using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef migrations add</c> only, so the tooling never starts the API (whose startup
/// connects to the configured database and migrates it). Adding a migration doesn't connect: the
/// connection string below is never used.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AgentDbContext>().UseNpgsql("Host=design-time;Database=design-time").Options);
}
