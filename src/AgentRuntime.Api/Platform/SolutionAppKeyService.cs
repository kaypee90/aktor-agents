using System.Text.Json;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Safety;
using AgentRuntime.Solutions;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// App keys (docs/solutions.md#app-keys): what a builder's backend holds instead of an organization
/// API key. One key reaches one installed solution through the apps API, and can only issue its
/// end-user tokens, read and write its records and start its workflows. It can't deploy, change or
/// remove anything, or reach another solution, so a leaked key exposes one app's data, not the
/// organization. Creating and revoking keys is audited.
/// </summary>
public sealed class SolutionAppKeyService(IDbContextFactory<AgentDbContext> dbFactory, ISolutionCatalog catalog, IAuditLog audit, TimeProvider clock,
    ILogger<SolutionAppKeyService> logger)
{
    public const int MaxKeysPerInstall = 20;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public sealed record AppKeyView(string KeyId, string Name, string Prefix, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

    /// <summary>The solution a request with an app key acts for.</summary>
    public sealed record AppCaller(string KeyId, string KeyName, InstalledSolution Solution)
    {
        public string TenantId => Solution.TenantId;
        public string InstallId => Solution.InstallId;
        public string Actor => $"app-key:{KeyId}";
    }

    private static AppKeyView ToView(SolutionAppKeyRecord k) => new(k.KeyId, k.Name, k.Prefix, k.CreatedBy, k.CreatedAt, k.LastUsedAt, k.RevokedAt);

    public async Task<IReadOnlyList<AppKeyView>> ListAsync(string tenantId, string installId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SolutionAppKeys.AsNoTracking().Where(k => k.TenantId == tenantId && k.InstallId == installId)
            .OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    /// <summary>A new key. The full key is returned once, here; only its hash is kept.</summary>
    public async Task<(AppKeyView Key, string Secret)> CreateAsync(string tenantId, string installId, string? name, string createdBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = await db.SolutionAppKeys.CountAsync(k => k.TenantId == tenantId && k.InstallId == installId && k.RevokedAt == null, ct);
        if (active >= MaxKeysPerInstall) throw new SolutionException($"A solution can have at most {MaxKeysPerInstall} active app keys; revoke one first.", StatusCodes.Status409Conflict);

        var (keyId, fullKey) = AppKeyFormat.New();
        var trimmed = name?.Trim();
        var row = new SolutionAppKeyRecord
        {
            KeyId = keyId,
            TenantId = tenantId,
            InstallId = installId,
            Name = string.IsNullOrEmpty(trimmed) ? "App key" : trimmed[..Math.Min(100, trimmed.Length)],
            Prefix = AppKeyFormat.Display(fullKey),
            SecretHash = SecretTokens.Hash(fullKey),
            CreatedBy = createdBy,
            CreatedAt = clock.GetUtcNow()
        };
        db.SolutionAppKeys.Add(row);
        await db.SaveChangesAsync(ct);
        await AuditAsync(tenantId, createdBy, "solution.app_key.created", row, ct);
        return (ToView(row), fullKey);
    }

    public async Task<bool> RevokeAsync(string tenantId, string installId, string keyId, string revokedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SolutionAppKeys.FirstOrDefaultAsync(k => k.TenantId == tenantId && k.InstallId == installId && k.KeyId == keyId, ct);
        if (row is null) return false;
        if (row.RevokedAt is null)
        {
            row.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await AuditAsync(tenantId, revokedBy, "solution.app_key.revoked", row, ct);
        }

        return true;
    }

    /// <summary>
    /// The solution a presented key acts for, or null when the key is unknown, revoked, for another
    /// install, or its solution isn't active. Checked in constant time against the stored hash.
    /// </summary>
    public async Task<AppCaller?> ResolveAsync(string? presented, string installId, CancellationToken ct)
    {
        if (!AppKeyFormat.TryParse(presented, out var keyId)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SolutionAppKeys.FindAsync([keyId], ct);
        if (row is null || row.RevokedAt is not null || row.InstallId != installId || !SecretTokens.HashEquals(presented!, row.SecretHash)) return null;
        var solution = await catalog.GetAsync(row.TenantId, row.InstallId, ct);
        if (solution is not { Status: SolutionInstallStatus.Active }) return null;

        var now = clock.GetUtcNow();
        if (row.LastUsedAt is null || now - row.LastUsedAt > TouchInterval)
        {
            row.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new AppCaller(row.KeyId, row.Name, solution);
    }

    private async Task AuditAsync(string tenantId, string actor, string action, SolutionAppKeyRecord row, CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Scope = Controllers.OrganizationPolicyController.AuditScope(tenantId),
                Key = $"{action}:{row.KeyId}",
                ActorType = "user",
                ActorId = actor,
                ActorName = actor,
                Action = action,
                Target = $"solution {row.InstallId}",
                Summary = $"App key '{row.Name}' ({row.Prefix})",
                DetailJson = JsonSerializer.Serialize(new { key_id = row.KeyId, install_id = row.InstallId, name = row.Name })
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The key change stands either way; the store logs its own failures too.
            logger.LogWarning(ex, "Couldn't audit {Action} for app key {KeyId}", action, row.KeyId);
        }
    }
}
