using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// Private workspaces (<see cref="WorkspaceVisibility.Private"/>): only their owner sees them. To
/// anyone else in the organization a private workspace, its runs (tasks), agents, events, files and
/// spend look exactly like things that don't exist (404, absent from lists), as another
/// organization's do. The owner is the person who created the workspace; an API key acts for the
/// person who created the key.
/// <para>Single lookups ask the workspace grain (authoritative); lists filter with the archived
/// <see cref="WorkspaceRecord.Visibility"/>, which is updated as soon as visibility changes.</para>
/// </summary>
public sealed class WorkspacePrivacy(IDbContextFactory<AgentDbContext> dbFactory, IGrainFactory grains, IHttpContextAccessor http)
{
    private const string Private = nameof(WorkspaceVisibility.Private);
    private static readonly object CacheKey = new();

    /// <summary>The caller of the current request; null outside one (background work sees everything).</summary>
    public Caller? CurrentCaller => http.HttpContext is { } ctx ? AktorAuthenticationHandler.CallerOf(ctx) : null;

    /// <summary>Every owner id the caller counts as: their user id, their API key, and the person
    /// who created that key.</summary>
    public async Task<HashSet<string>> IdentitiesAsync(Caller caller, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (caller.UserId is { Length: > 0 } user) ids.Add(user);
        if (caller.ApiKeyId is { Length: > 0 } key)
        {
            ids.Add($"key:{key}");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var creator = await db.ApiKeys.AsNoTracking().Where(k => k.KeyId == key && k.TenantId == caller.TenantId)
                .Select(k => k.CreatedByUserId).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(creator)) ids.Add(creator);
        }

        return ids;
    }

    /// <summary>Whether the caller owns something owned by <paramref name="ownerId"/>, including
    /// things made with an API key the caller created.</summary>
    public async Task<bool> OwnsAsync(Caller caller, string ownerId, CancellationToken ct = default)
    {
        var ids = await IdentitiesAsync(caller, ct);
        if (ids.Contains(ownerId)) return true;
        if (!ownerId.StartsWith("key:", StringComparison.Ordinal) || caller.UserId is not { Length: > 0 } user) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keyId = ownerId[4..];
        return await db.ApiKeys.AsNoTracking().AnyAsync(k => k.KeyId == keyId && k.TenantId == caller.TenantId && k.CreatedByUserId == user, ct);
    }

    /// <summary>Whether <paramref name="caller"/> may see the workspace at all: same organization,
    /// and either visible to the organization or theirs.</summary>
    public async Task<bool> CanSeeWorkspaceAsync(Caller caller, string workspaceId, CancellationToken ct = default)
    {
        if (!WorkspaceIds.IsWorkspace(workspaceId)) return false;
        var info = await grains.GetGrain<IWorkspaceGrain>(workspaceId).GetAccessInfo();
        if (info is null || !TenantIds.Same(info.TenantId, caller.TenantId)) return false;
        return info.Visibility != WorkspaceVisibility.Private || await OwnsAsync(caller, info.OwnerId, ct);
    }

    public async Task<bool> IsOwnerAsync(Caller caller, string workspaceId, CancellationToken ct = default)
    {
        var info = WorkspaceIds.IsWorkspace(workspaceId) ? await grains.GetGrain<IWorkspaceGrain>(workspaceId).GetAccessInfo() : null;
        return info is not null && TenantIds.Same(info.TenantId, caller.TenantId) && await OwnsAsync(caller, info.OwnerId, ct);
    }

    /// <summary>The organization's private workspaces that aren't the caller's: everything under them
    /// is left out of what the caller sees. Cached for the request.</summary>
    /// <param name="fresh">Skip the request's cache (for long requests such as the live event stream).</param>
    public async Task<HashSet<string>> HiddenWorkspacesAsync(Caller caller, CancellationToken ct = default, bool fresh = false)
    {
        if (!fresh && http.HttpContext?.Items[CacheKey] is HashSet<string> cached) return cached;
        var ids = await IdentitiesAsync(caller, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owners = await db.Workspaces.AsNoTracking()
            .Where(w => w.TenantId == caller.TenantId && w.Visibility == Private)
            .Select(w => new { w.WorkspaceId, w.OwnerId })
            .ToListAsync(ct);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in owners)
        {
            if (!ids.Contains(w.OwnerId) && !await OwnsAsync(caller, w.OwnerId, ct)) hidden.Add(w.WorkspaceId);
        }

        if (http.HttpContext is { } ctx) ctx.Items[CacheKey] = hidden;
        return hidden;
    }

    /// <summary>The hidden workspaces for the current request's caller; empty outside a request.</summary>
    public async Task<HashSet<string>> HiddenForCurrentCallerAsync(CancellationToken ct = default) =>
        CurrentCaller is { } caller ? await HiddenWorkspacesAsync(caller, ct) : [];

    /// <summary>Whether a task (a run of a workspace, or the workspace's own id) is hidden from the caller.</summary>
    public static bool Hides(HashSet<string> hidden, string? taskId, string? workspaceId) =>
        hidden.Count > 0 && ((workspaceId is not null && hidden.Contains(workspaceId)) || (taskId is not null && hidden.Contains(taskId)));
}
