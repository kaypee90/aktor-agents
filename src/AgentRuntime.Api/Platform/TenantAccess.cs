using AgentRuntime.Agents;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// Checks that a resource belongs to the caller's organization, and isn't part of someone else's
/// private workspace (<see cref="WorkspacePrivacy"/>). Controllers answer 404 when it doesn't, so
/// such ids are indistinguishable from ids that don't exist.
/// </summary>
public sealed class TenantAccess(IHttpContextAccessor http, IAgentOrchestrator orchestrator, AgentDbContext db, WorkspacePrivacy privacy)
{
    public string TenantId => http.HttpContext!.Caller().TenantId;

    private Infrastructure.Identity.Caller Caller => http.HttpContext!.Caller();

    public Task<bool> WorkspaceAsync(string workspaceId) => privacy.CanSeeWorkspaceAsync(Caller, workspaceId);

    /// <summary>Private workspaces in the caller's organization that aren't theirs.</summary>
    public Task<HashSet<string>> HiddenWorkspacesAsync(CancellationToken ct = default) => privacy.HiddenWorkspacesAsync(Caller, ct);

    /// <summary>Everything under hidden workspaces that events and agents are grouped by: the
    /// workspaces' ids and their runs' (task) ids.</summary>
    public async Task<HashSet<string>> HiddenScopesAsync(CancellationToken ct = default)
    {
        var hidden = await HiddenWorkspacesAsync(ct);
        if (hidden.Count == 0) return hidden;
        var runs = await db.Tasks.AsNoTracking()
            .Where(t => t.TenantId == TenantId && t.WorkspaceId != null && hidden.Contains(t.WorkspaceId))
            .Select(t => t.TaskId).ToListAsync(ct);
        return [.. hidden, .. runs];
    }

    /// <summary>Whether the caller owns the workspace (only the owner may change its visibility).</summary>
    public Task<bool> OwnsWorkspaceAsync(string workspaceId) => privacy.IsOwnerAsync(Caller, workspaceId);

    public async Task<bool> AgentAsync(string agentId)
    {
        var snapshot = await orchestrator.GetSnapshotAsync(agentId);
        if (snapshot is null || !TenantIds.Same(snapshot.TenantId, TenantId)) return false;
        var hidden = await HiddenWorkspacesAsync();
        if (WorkspacePrivacy.Hides(hidden, snapshot.TaskId, snapshot.WorkspaceId)) return false;
        // A pipeline run's agents: their task is the run, whose row says which workspace it belongs to.
        return hidden.Count == 0 || !await db.Tasks.AsNoTracking()
            .AnyAsync(t => t.TaskId == snapshot.TaskId && t.WorkspaceId != null && hidden.Contains(t.WorkspaceId));
    }

    public async Task<bool> TaskAsync(string taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks.AsNoTracking().Where(t => t.TaskId == taskId && t.TenantId == TenantId)
            .Select(t => new { t.TaskId, t.WorkspaceId }).FirstOrDefaultAsync(ct);
        return task is not null && !WorkspacePrivacy.Hides(await HiddenWorkspacesAsync(ct), task.TaskId, task.WorkspaceId);
    }

    /// <summary>A task or workspace id (events and agents are grouped under both).</summary>
    public async Task<bool> ScopeAsync(string id, CancellationToken ct = default) =>
        WorkspaceIds.IsWorkspace(id) ? await WorkspaceAsync(id)
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

/// <summary>Answers 404 for a task, workspace or world (route values "id" and "otherId") the caller
/// may not see: another organization's, or part of someone else's private workspace.</summary>
public sealed class ScopeAccessAttribute() : Microsoft.AspNetCore.Mvc.TypeFilterAttribute(typeof(ScopeAccessFilter));

public sealed class ScopeAccessFilter(TenantAccess access) : Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        foreach (var key in new[] { "id", "otherId" })
        {
            if (context.RouteData.Values.TryGetValue(key, out var v) && v is string id &&
                !await access.ScopeAsync(id, context.HttpContext.RequestAborted))
            {
                context.Result = new Microsoft.AspNetCore.Mvc.NotFoundResult();
                return;
            }
        }

        await next();
    }
}
