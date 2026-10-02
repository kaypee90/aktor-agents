using AgentRuntime.Agents;
using AgentRuntime.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using AgentRuntime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Controllers;

[ApiController]
[Route("api/agents")]
public sealed class AgentsController(IAgentOrchestrator orchestrator, AgentDbContext db, TenantAccess access) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var agents = await orchestrator.FindAgentsAsync(new AgentRuntime.Contracts.FindAgentsQuery { TenantId = access.TenantId }, ct);
        // Agents given their own model at spawn (docs/llm-settings.md); the rest follow their task's.
        var ids = agents.Select(a => a.AgentId).ToList();
        var models = await db.Agents.AsNoTracking()
            .Where(a => ids.Contains(a.AgentId) && a.ModelProfileId != null)
            .ToDictionaryAsync(a => a.AgentId, a => a.ModelProfileId, ct);
        return Ok(agents.Select(a => new
        {
            model_profile_id = models.GetValueOrDefault(a.AgentId),
            agent_id = a.AgentId,
            role = a.Role,
            goal = a.Goal,
            status = a.Status.ToString(),
            capabilities = a.Capabilities,
            parent_agent_id = a.ParentAgentId,
            root_agent_id = a.RootAgentId,
            depth = a.Depth
        }));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var snapshot = await orchestrator.GetSnapshotAsync(id, ct);
        return snapshot is null || !AgentRuntime.Tenancy.TenantIds.Same(snapshot.TenantId, access.TenantId) ? NotFound() : Ok(snapshot);
    }

    [HttpGet("{id}/children")]
    public async Task<IActionResult> Children(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        var children = await orchestrator.ListChildrenAsync(id, ct);
        return Ok(children);
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> Messages(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.FromAgentId == id || m.ToAgentId == id)
            .OrderBy(m => m.Timestamp)
            .Take(500)
            .ToListAsync(ct);

        return Ok(messages);
    }

    [HttpGet("{id}/tool-calls")]
    public async Task<IActionResult> ToolCalls(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        var calls = await db.ToolCalls.AsNoTracking()
            .Where(t => t.AgentId == id)
            .OrderBy(t => t.Timestamp)
            .Take(500)
            .ToListAsync(ct);

        return Ok(calls);
    }

    [HttpPost("{id}/pause")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Pause(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        await orchestrator.PauseAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id}/resume")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Resume(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        await orchestrator.ResumeAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id}/terminate")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Terminate(string id, CancellationToken ct)
    {
        if (!await access.AgentAsync(id)) return NotFound();
        await orchestrator.StopAsync(id, ct);
        return NoContent();
    }
}
