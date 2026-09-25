using System.IO.Compression;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

public sealed record CreateTaskRequest(string Goal, ResourceBudget? Budget);

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(IAgentOrchestrator orchestrator, AgentDbContext db, IOptions<ToolsOptions> toolsOptions, TenantAccess access) : ControllerBase
{
    /// <summary>Submits a high-level human goal (CLAUDE.md section 1). This is the only manual step —
    /// everything after this is autonomous.</summary>
    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Create([FromBody] CreateTaskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            return BadRequest(new { error = "goal is required" });
        }

        var taskId = Guid.NewGuid().ToString("n");
        var rootAgentId = await orchestrator.CreateRootAgentAsync(taskId, request.Goal, request.Budget, access.TenantId, ct);

        return CreatedAtAction(nameof(Get), new { id = taskId }, new { task_id = taskId, root_agent_id = rootAgentId });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id && t.TenantId == access.TenantId, ct);
        if (task is null) return NotFound();

        AgentSnapshot? rootSnapshot = task.RootAgentId is null
            ? null
            : await orchestrator.GetSnapshotAsync(task.RootAgentId, ct);

        return Ok(new
        {
            task_id = task.TaskId,
            goal = task.Goal,
            status = rootSnapshot?.Status.ToString() ?? task.Status,
            root_agent_id = task.RootAgentId,
            created_at = task.CreatedAt,
            completed_at = task.CompletedAt,
            result_summary = task.ResultSummary
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.TenantId == access.TenantId).OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync(ct);
        return Ok(tasks.Select(t => new { task_id = t.TaskId, goal = t.Goal, status = t.Status, created_at = t.CreatedAt }));
    }

    [HttpPost("{id}/pause")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Pause(string id, CancellationToken ct) => await ForEachAgentInTask(id, orchestrator.PauseAsync, ct);

    [HttpPost("{id}/resume")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Resume(string id, CancellationToken ct) => await ForEachAgentInTask(id, orchestrator.ResumeAsync, ct);

    [HttpPost("{id}/cancel")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policies.Member)]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct) => await ForEachAgentInTask(id, orchestrator.StopAsync, ct);

    /// <summary>The aggregated final result (CLAUDE.md section 52): the root's own summary plus
    /// every other agent's completion summary, every artifact produced, and run metrics — not
    /// just the one-line root summary <see cref="Get"/> returns.</summary>
    [HttpGet("{id}/result")]
    public async Task<IActionResult> Result(string id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id && t.TenantId == access.TenantId, ct);
        if (task is null) return NotFound();

        if (task.ResultJson is null)
        {
            return Ok(new { ready = false, status = task.Status });
        }

        using var doc = JsonDocument.Parse(task.ResultJson);
        return Ok(new { ready = true, result = doc.RootElement.Clone() });
    }

    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> Artifacts(string id, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new
            {
                artifact_id = a.ArtifactId,
                type = a.Type,
                file_name = Path.GetFileName(a.Location),
                created_by_agent = a.CreatedByAgent,
                created_at = a.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(artifacts);
    }

    /// <summary>Streams an artifact's file content. Only files under the task's own sandboxed
    /// workspace directory can ever be referenced here (see WorkspacePath in the filesystem tools),
    /// so this can't be used to read arbitrary paths on the API container.</summary>
    [HttpGet("{id}/artifacts/{artifactId}/content")]
    public async Task<IActionResult> ArtifactContent(string id, string artifactId, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TaskId == id && a.ArtifactId == artifactId, ct);
        if (artifact is null) return NotFound();

        if (!System.IO.File.Exists(artifact.Location))
        {
            return NotFound(new { error = "Artifact file no longer exists on disk." });
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(artifact.Location, ct);
        return File(bytes, "application/octet-stream", Path.GetFileName(artifact.Location));
    }

    /// <summary>
    /// Every artifact file the task produced, as one zip. Entries keep their folder layout relative
    /// to the task workspace (e.g. src/main.py). A file an agent wrote several times appears once,
    /// with its final contents. Only files inside this task's own workspace are included.
    /// </summary>
    [HttpGet("{id}/artifacts.zip")]
    public async Task<IActionResult> ArtifactsZip(string id, CancellationToken ct)
    {
        if (!await access.TaskAsync(id, ct)) return NotFound();
        var locations = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == id)
            .Select(a => a.Location)
            .Distinct()
            .ToListAsync(ct);

        var opts = toolsOptions.Value;
        var files = locations
            .Where(l => WorkspacePath.IsInsideTaskRoot(opts, id, l) && System.IO.File.Exists(l))
            .ToList();
        if (files.Count == 0)
        {
            return NotFound(new { error = "This task has no artifact files to download." });
        }

        var taskRoot = WorkspacePath.TaskRoot(opts, id);
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entryName = Path.GetRelativePath(taskRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = await entry.OpenAsync(ct);
                await using var source = System.IO.File.OpenRead(file);
                await source.CopyToAsync(entryStream, ct);
            }
        }

        buffer.Position = 0;
        return File(buffer, "application/zip", $"task-{id[..Math.Min(8, id.Length)]}-artifacts.zip");
    }

    [HttpGet("{id}/events")]
    public async Task<IActionResult> Events(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!await access.ScopeAsync(id, ct)) return NotFound();
        var events = await db.Events.AsNoTracking()
            .Where(e => e.TaskId == id && e.TenantId == access.TenantId)
            .OrderBy(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);

        return Ok(events);
    }

    private async Task<IActionResult> ForEachAgentInTask(string taskId, Func<string, CancellationToken, Task> action, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == taskId, ct);
        if (task is null) return NotFound();

        var agents = await db.Agents.AsNoTracking().Where(a => a.TaskId == taskId).Select(a => a.AgentId).ToListAsync(ct);
        foreach (var agentId in agents)
        {
            await action(agentId, ct);
        }

        return NoContent();
    }
}
