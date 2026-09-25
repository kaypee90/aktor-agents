using AgentRuntime.Agents;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Simulation;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// Checks that a resource belongs to the caller's organization. Controllers answer 404 when it
/// doesn't, so another tenant's ids are indistinguishable from ids that don't exist.
/// </summary>
public sealed class TenantAccess(IHttpContextAccessor http, IGrainFactory grains, IAgentOrchestrator orchestrator, AgentDbContext db)
{
    public string TenantId => http.HttpContext!.Caller().TenantId;

    public async Task<bool> WorkspaceAsync(string workspaceId) =>
        WorkspaceIds.IsWorkspace(workspaceId) && TenantIds.Same(await grains.GetGrain<IWorkspaceGrain>(workspaceId).GetTenantId() ?? "\0", TenantId);

    public async Task<bool> WorldAsync(string worldId)
    {
        if (!WorldIds.IsWorld(worldId)) return false;
        var snapshot = await grains.GetGrain<IWorldGrain>(worldId).GetSnapshot();
        if (snapshot is not null) return TenantIds.Same(snapshot.TenantId, TenantId);
        // Not running (e.g. after a restart): the archived copy says whose it was.
        return await db.Worlds.AsNoTracking().AnyAsync(w => w.WorldId == worldId && w.TenantId == TenantId);
    }

    public async Task<bool> AgentAsync(string agentId)
    {
        var snapshot = await orchestrator.GetSnapshotAsync(agentId);
        return snapshot is not null && TenantIds.Same(snapshot.TenantId, TenantId);
    }

    public Task<bool> TaskAsync(string taskId, CancellationToken ct = default) =>
        db.Tasks.AsNoTracking().AnyAsync(t => t.TaskId == taskId && t.TenantId == TenantId, ct);

    /// <summary>A task, workspace or world id (events and agents are grouped under all three).</summary>
    public async Task<bool> ScopeAsync(string id, CancellationToken ct = default) =>
        WorkspaceIds.IsWorkspace(id) ? await WorkspaceAsync(id)
        : WorldIds.IsWorld(id) ? await WorldAsync(id)
        : await TaskAsync(id, ct);
}

/// <summary>Answers 404 for any workspace (route value "id" or "workspaceId") outside the caller's
/// organization, before the action runs.</summary>
public sealed class WorkspaceAccessAttribute() : Microsoft.AspNetCore.Mvc.TypeFilterAttribute(typeof(WorkspaceAccessFilter));

public sealed class WorkspaceAccessFilter(TenantAccess access) : Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        var id = context.RouteData.Values.TryGetValue("workspaceId", out var w) ? w as string
            : context.RouteData.Values.TryGetValue("id", out var i) ? i as string
            : null;
        if (id is not null && !await access.WorkspaceAsync(id))
        {
            context.Result = new Microsoft.AspNetCore.Mvc.NotFoundResult();
            return;
        }

        await next();
    }
}
