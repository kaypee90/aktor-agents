using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>Historical event queries. The live feed is served separately over SSE at /ws/events.</summary>
[ApiController]
[Route("api/events")]
public sealed class EventsController(AgentDbContext db, TenantAccess access) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? taskId, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var query = db.Events.AsNoTracking().Where(e => e.TenantId == access.TenantId);
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            // A workspace's history includes its most recent runs' events (published under the run).
            var scopes = new List<string> { taskId };
            if (AgentRuntime.Workspaces.WorkspaceIds.IsWorkspace(taskId))
            {
                scopes.AddRange(await db.Tasks.AsNoTracking()
                    .Where(t => t.TenantId == access.TenantId && t.WorkspaceId == taskId)
                    .OrderByDescending(t => t.CreatedAt).Take(5).Select(t => t.TaskId).ToListAsync(ct));
            }

            query = query.Where(e => e.TaskId != null && scopes.Contains(e.TaskId));
        }

        var events = await query.OrderByDescending(e => e.Timestamp).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
        events.Reverse();
        return Ok(events);
    }
}
