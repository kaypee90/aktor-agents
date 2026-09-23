using AgentRuntime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

/// <summary>Historical event queries. The live feed is served separately over SSE at /ws/events.</summary>
[ApiController]
[Route("api/events")]
public sealed class EventsController(AgentDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? taskId, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var query = db.Events.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            query = query.Where(e => e.TaskId == taskId);
        }

        var events = await query.OrderByDescending(e => e.Timestamp).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
        events.Reverse();
        return Ok(events);
    }
}
