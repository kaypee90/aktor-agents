using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>Installed solutions, read from Postgres and cached briefly: records tools read the
/// manifest on every call. An upgrade on this server invalidates at once; other servers see it
/// within <see cref="Ttl"/>.</summary>
public sealed class EfSolutionCatalog(IDbContextFactory<AgentDbContext> dbFactory, TimeProvider clock) : ISolutionCatalog
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (InstalledSolution? Solution, DateTimeOffset At)> _cache = new();

    public async Task<InstalledSolution?> GetAsync(string tenantId, string installId, CancellationToken ct = default) =>
        await FindAsync(installId, ct) is { } s && Tenancy.TenantIds.Same(s.TenantId, tenantId) ? s : null;

    public async Task<InstalledSolution?> FindAsync(string installId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!_cache.TryGetValue(installId, out var cached) || now - cached.At > Ttl)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.SolutionInstalls.AsNoTracking().FirstOrDefaultAsync(s => s.InstallId == installId, ct);
            cached = (row is null ? null : ToSolution(row), now);
            _cache[installId] = cached;
        }

        return cached.Solution;
    }

    public void Invalidate(string installId) => _cache.TryRemove(installId, out _);

    public static InstalledSolution ToSolution(SolutionInstallRecord row) => new()
    {
        InstallId = row.InstallId,
        TenantId = row.TenantId,
        Manifest = JsonSerializer.Deserialize<SolutionManifest>(row.ManifestJson, SolutionManifestReader.Json) ?? new SolutionManifest(),
        Workspaces = JsonSerializer.Deserialize<Dictionary<string, string>>(row.WorkspacesJson) ?? [],
        Status = Enum.TryParse<SolutionInstallStatus>(row.Status, out var status) ? status : SolutionInstallStatus.Uninstalled
    };
}
