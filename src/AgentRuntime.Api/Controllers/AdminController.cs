using AgentRuntime.Agents;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Administrative controls outside the normal per-task human controls in CLAUDE.md section 32 —
/// specifically, wiping all runtime state to start over. Not gated behind auth in this prototype;
/// a production deployment would restrict this to an operator role.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    IAgentOrchestrator orchestrator,
    AgentDbContext db,
    AgentDatabaseSandbox databaseSandbox,
    IGrainFactory grainFactory,
    IOptions<ToolsOptions> toolsOptions) : ControllerBase
{
    /// <summary>Clears every task, agent, message, event, tool call, artifact, memory entry, and
    /// agent scratch table, plus the live agent registry, so the next submitted goal starts from a clean slate. Existing
    /// agent grain activations (if any are still mid-turn) are left to idle out naturally — nothing
    /// references their old ids once the registry and history are both cleared.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        // Stop running worlds first, or their clocks would keep waking residents after the reset.
        var liveWorlds = await db.Worlds.Where(w => w.Status != nameof(WorldStatus.Ended)).Select(w => w.WorldId).ToListAsync(ct);
        foreach (var worldId in liveWorlds)
        {
            await grainFactory.GetGrain<IWorldGrain>(worldId).End("reset by the operator");
        }

        await orchestrator.ResetRegistryAsync(ct);

        await db.Tasks.ExecuteDeleteAsync(ct);
        await db.Agents.ExecuteDeleteAsync(ct);
        await db.Messages.ExecuteDeleteAsync(ct);
        await db.Events.ExecuteDeleteAsync(ct);
        await db.ToolCalls.ExecuteDeleteAsync(ct);
        await db.Artifacts.ExecuteDeleteAsync(ct);
        await db.MemoryEntries.ExecuteDeleteAsync(ct);
        await db.Worlds.ExecuteDeleteAsync(ct);

        await databaseSandbox.ResetAsync(ct);

        var workspaceRoot = toolsOptions.Value.WorkspaceRoot;
        if (Directory.Exists(workspaceRoot))
        {
            foreach (var dir in Directory.GetDirectories(workspaceRoot))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* best effort — a file may still be open */ }
            }
        }

        return NoContent();
    }
}
