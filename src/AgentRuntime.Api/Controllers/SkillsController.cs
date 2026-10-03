using AgentRuntime.Api.Platform;
using AgentRuntime.Skills;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's skills (docs/skills.md): written in the dashboard or uploaded as a SKILL.md
/// or .zip, then available to every agent of the organization. Anyone can read them; changing them
/// needs the Admin role, since a skill changes how every agent works. With ?workspace=ws-… every
/// endpoint works on that workspace's own skills instead, which only its agents use.
/// </summary>
[ApiController]
[Route("api/skills")]
public sealed class SkillsController(ISkillStore skills, TenantAccess access, IOptions<SkillOptions> options) : ControllerBase
{
    public sealed record SkillFileBody(string Path, string Content);
    public sealed record WriteSkillBody(string Name, string Description, string Instructions, List<SkillFileBody>? Files);
    public sealed record EditSkillBody(string Description, string Instructions, List<SkillFileBody>? Files);
    public sealed record EnableBody(bool Enabled);

    /// <summary>?workspace=ws-…: that workspace's own skills (it must be the caller's organization's).</summary>
    private async Task<(bool Ok, string? WorkspaceId)> ScopeAsync()
    {
        var workspace = Request.Query["workspace"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(workspace)) return (true, null);
        return await access.WorkspaceAsync(workspace) ? (true, workspace) : (false, null);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        return ok ? Ok(await skills.ListAsync(access.TenantId, enabledOnly: false, ws, ct)) : NotFound();
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        var skill = await skills.GetAsync(access.TenantId, name, ws, ct);
        return skill is null ? NotFound() : Ok(View(skill));
    }

    /// <summary>The skill as a file: its SKILL.md, or (?format=zip) a zip with its resource files.</summary>
    [HttpGet("{name}/download")]
    public async Task<IActionResult> Download(string name, [FromQuery] string? format, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        var skill = await skills.GetAsync(access.TenantId, name, ws, ct);
        if (skill is null) return NotFound();
        return format == "zip" || skill.Files.Count > 0 && format != "md"
            ? File(SkillPackage.ToZip(skill), "application/zip", $"{skill.Name}.zip")
            : File(System.Text.Encoding.UTF8.GetBytes(SkillPackage.ToMarkdown(skill)), "text/markdown", "SKILL.md");
    }

    /// <summary>Writes a new skill. 409 if the name is taken (edit it with PUT instead).</summary>
    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] WriteSkillBody body, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        if (await skills.GetAsync(access.TenantId, body.Name?.Trim() ?? string.Empty, ws, ct) is not null)
        {
            return Conflict(new { error = $"There's already a skill named '{body.Name}'. Edit it, or pick another name." });
        }

        return await SaveAsync(() => SkillPackage.FromParts(body.Name ?? string.Empty, body.Description ?? string.Empty, body.Instructions ?? string.Empty,
            body.Files?.Select(f => new SkillFile(f.Path, f.Content)), options.Value), ws, ct);
    }

    /// <summary>Edits a skill, saving it as a new version. The name stays.</summary>
    [HttpPut("{name}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Edit(string name, [FromBody] EditSkillBody body, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        var existing = await skills.GetAsync(access.TenantId, name, ws, ct);
        if (existing is null) return NotFound();
        return await SaveAsync(() => SkillPackage.FromParts(existing.Name, body.Description ?? string.Empty, body.Instructions ?? string.Empty,
            body.Files?.Select(f => new SkillFile(f.Path, f.Content)) ?? existing.Files, options.Value), ws, ct);
    }

    /// <summary>Uploads a SKILL.md or a .zip (SKILL.md plus resource files). A skill with the same
    /// name is replaced by the upload as a new version.</summary>
    [HttpPost("upload")]
    [Authorize(Policies.Admin)]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Attach a SKILL.md or a .zip as the form field 'file'." });
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();

        await using var stream = file.OpenReadStream();
        var isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.ContentType is "application/zip" or "application/x-zip-compressed";
        if (isZip)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return await SaveAsync(() => SkillPackage.FromZip(buffer, options.Value), ws, ct);
        }

        if (file.Length > options.Value.MaxTotalBytes) return BadRequest(new { error = "That file is too large for a skill." });
        using var reader = new StreamReader(stream);
        var markdown = await reader.ReadToEndAsync(ct);
        return await SaveAsync(() => SkillPackage.FromMarkdown(markdown, options.Value), ws, ct);
    }

    [HttpPatch("{name}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetEnabled(string name, [FromBody] EnableBody body, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        return ok && await skills.SetEnabledAsync(access.TenantId, name, body.Enabled, ws, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("{name}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        var (ok, ws) = await ScopeAsync();
        return ok && await skills.DeleteAsync(access.TenantId, name, ws, ct) ? NoContent() : NotFound();
    }

    private async Task<IActionResult> SaveAsync(Func<SkillDocument> build, string? workspaceId, CancellationToken ct)
    {
        SkillDocument skill;
        try
        {
            skill = build();
        }
        catch (SkillPackageException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var who = HttpContext.Caller() is var c && c.Email is { } email ? email : c.ActorId;
        var saved = await skills.SaveAsync(access.TenantId, skill with { UpdatedBy = who }, workspaceId, ct);
        return Ok(View(saved));
    }

    private static object View(SkillDocument s) => new
    {
        name = s.Name,
        description = s.Description,
        instructions = s.Instructions,
        files = s.Files.Select(f => new { path = f.Path, content = f.Content }),
        version = s.Version,
        enabled = s.Enabled,
        updated_at = s.UpdatedAt,
        updated_by = s.UpdatedBy,
        workspace_id = s.WorkspaceId
    };
}
