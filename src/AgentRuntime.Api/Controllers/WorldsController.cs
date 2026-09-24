using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Living-world simulations: a seed description becomes a world of autonomous residents that
/// plan, talk, move, trade energy and vote on their own, bounded by a tick and time limit.
/// </summary>
[ApiController]
[Route("api/worlds")]
public sealed class WorldsController(
    IGrainFactory grainFactory,
    IWorldGenesis genesis,
    AgentDbContext db,
    IOptions<SimulationOptions> options,
    ILogger<WorldsController> logger) : ControllerBase
{
    public sealed record CreateWorldRequest(
        string Seed,
        int? Population,
        int? TickIntervalSeconds,
        int? MaxTicks,
        int? MaxDurationMinutes);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorldRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Seed))
        {
            return BadRequest(new { error = "seed is required" });
        }

        var o = options.Value;
        // Clamped server-side: these are the simulation's cost bound, so the client can't lift them.
        var population = Math.Clamp(request.Population ?? 5, 1, o.MaxInitialPopulation);
        var settings = new WorldSettings
        {
            Seed = request.Seed.Trim(),
            TickIntervalSeconds = Math.Clamp(request.TickIntervalSeconds ?? o.DefaultTickIntervalSeconds, o.MinTickIntervalSeconds, 600),
            MaxTicks = Math.Clamp(request.MaxTicks ?? o.DefaultMaxTicks, 1, o.MaxTicksCeiling),
            MaxDurationMinutes = Math.Clamp(request.MaxDurationMinutes ?? o.DefaultMaxDurationMinutes, 1, o.MaxDurationMinutesCeiling)
        };

        WorldBlueprint blueprint;
        try
        {
            blueprint = await genesis.GenerateAsync(settings.Seed, population, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "World genesis failed");
            return StatusCode(502, new { error = $"World generation failed: {ex.Message}" });
        }

        var worldId = WorldIds.New();
        var world = grainFactory.GetGrain<IWorldGrain>(worldId);
        await world.Create(blueprint, settings);
        await world.Start();

        return Ok(new { world_id = worldId });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var worlds = await db.Worlds.AsNoTracking()
            .OrderByDescending(w => w.CreatedAt)
            .Take(50)
            .Select(w => new
            {
                world_id = w.WorldId,
                name = w.Name,
                seed = w.Seed,
                status = w.Status,
                tick = w.Tick,
                max_ticks = w.MaxTicks,
                residents = w.Residents,
                cost_usd = w.CostUsd,
                created_at = w.CreatedAt,
                ended_at = w.EndedAt
            })
            .ToListAsync(ct);

        return Ok(worlds);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!WorldIds.IsWorld(id)) return NotFound();

        var snapshot = await grainFactory.GetGrain<IWorldGrain>(id).GetSnapshot();
        if (snapshot is not null) return Ok(snapshot);

        // Not in memory (e.g. the server restarted): serve the last archived snapshot, marked
        // ended since nothing is running it any more.
        var record = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.WorldId == id, ct);
        if (record is null) return NotFound();

        var archived = System.Text.Json.Nodes.JsonNode.Parse(record.SnapshotJson);
        if (archived is System.Text.Json.Nodes.JsonObject obj && obj["status"]?.GetValue<string>() != nameof(WorldStatus.Ended))
        {
            obj["status"] = nameof(WorldStatus.Ended);
            obj["end_reason"] = "The server restarted; this is the last saved state of the world.";
        }

        return Content(archived?.ToJsonString() ?? "{}", "application/json");
    }

    [HttpPost("{id}/pause")]
    public async Task<IActionResult> Pause(string id)
    {
        if (!WorldIds.IsWorld(id)) return NotFound();
        await grainFactory.GetGrain<IWorldGrain>(id).Pause();
        return NoContent();
    }

    [HttpPost("{id}/resume")]
    public async Task<IActionResult> Resume(string id)
    {
        if (!WorldIds.IsWorld(id)) return NotFound();
        await grainFactory.GetGrain<IWorldGrain>(id).Resume();
        return NoContent();
    }

    [HttpPost("{id}/end")]
    public async Task<IActionResult> End(string id)
    {
        if (!WorldIds.IsWorld(id)) return NotFound();
        await grainFactory.GetGrain<IWorldGrain>(id).End("ended by the operator");
        return NoContent();
    }
}
