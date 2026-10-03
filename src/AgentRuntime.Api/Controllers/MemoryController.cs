using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's shared knowledge (docs/memory.md): what agents saved with
/// write_memory(shared=true), searchable by meaning and words, plus facts people add for them.
/// With ?workspace=ws-… every endpoint works on that workspace's own knowledge, which only its
/// agents can find.
/// </summary>
[ApiController]
[Route("api/memory")]
public sealed class MemoryController(IMemoryStore memory, IEmbeddingProvider embeddings, TenantAccess access,
    Microsoft.Extensions.Options.IOptions<AgentRuntime.Infrastructure.Tools.ToolsOptions> toolsOptions) : ControllerBase
{
    /// <summary>Each knowledge entry made from a file holds about this much text, so a search finds
    /// the passage that matters rather than a whole document.</summary>
    private const int ChunkChars = 4_000;

    /// <summary>At most this much of one file becomes knowledge.</summary>
    private const int MaxFileChars = 200_000;

    public sealed record AddKnowledgeBody(string Key, string Value);

    /// <summary>?workspace=ws-…: that workspace's own knowledge (it must be the caller's organization's).</summary>
    private async Task<(bool Ok, string? WorkspaceId)> ScopeAsync()
    {
        var workspace = Request.Query["workspace"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(workspace)) return (true, null);
        return await access.WorkspaceAsync(workspace) ? (true, workspace) : (false, null);
    }

    private static MemoryScope ScopeOf(string? workspaceId) => workspaceId is null ? MemoryScope.Organization : MemoryScope.OnlyWorkspace(workspaceId);

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        semantic = embeddings.IsConfigured,
        embedding_model = embeddings.IsConfigured ? embeddings.Model : null,
        mode = embeddings.IsConfigured ? "hybrid (meaning, words and recency)" : "keywords and recency"
    });

    /// <summary>Shared knowledge, best matches first (an empty query lists the most recent).</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        var results = await memory.SearchAsync(access.TenantId, q ?? string.Empty, MemoryKind.Shared, scope: ScopeOf(ws), cancellationToken: ct);
        return Ok(results.Take(Math.Clamp(limit, 1, 200)).Select(r => new
        {
            memory_id = r.MemoryId,
            key = r.Key,
            value = r.Value,
            agent_id = r.AgentId,
            created_at = r.CreatedAt,
            score = r.Score,
            workspace_id = r.WorkspaceId
        }));
    }

    /// <summary>Adds a fact every agent of the organization can find with search_knowledge.</summary>
    [HttpPost]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Add([FromBody] AddKnowledgeBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Key) || string.IsNullOrWhiteSpace(body.Value)) return BadRequest(new { error = "key and value are required" });
        if (body.Key.Length > 200 || body.Value.Length > 20_000) return BadRequest(new { error = "key is at most 200 characters, value at most 20,000" });
        var (ok, ws) = await ScopeAsync();
        if (!ok) return NotFound();
        await memory.WriteAsync(new MemoryRecord
        {
            TenantId = access.TenantId,
            AgentId = "user",
            Kind = MemoryKind.Shared,
            Key = body.Key.Trim(),
            Value = body.Value.Trim(),
            WorkspaceId = ws
        }, ct);
        return NoContent();
    }

    /// <summary>
    /// Adds files as knowledge (multipart field "files"): their text — PDF, Word, Excel, PowerPoint,
    /// CSV, Markdown, plain text and code — is split into passages, each an entry keyed by the file
    /// name ("report.pdf (part 2 of 5)"). Uploading a file with the same name again replaces its
    /// passages. Files with no readable text (images, other binaries) are reported, not added.
    /// </summary>
    [HttpPost("files")]
    [Authorize(Policies.Member)]
    [RequestSizeLimit(300L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300L * 1024 * 1024)]
    public async Task<IActionResult> AddFiles([FromForm] List<IFormFile> files, CancellationToken ct)
    {
        var opts = toolsOptions.Value;
        var (scoped, ws) = await ScopeAsync();
        if (!scoped) return NotFound();
        if (files.Count == 0) return BadRequest(new { error = "Choose at least one file." });
        if (files.Count > opts.AttachmentMaxFiles) return BadRequest(new { error = $"Add at most {opts.AttachmentMaxFiles} files at a time." });
        if (files.FirstOrDefault(f => f.Length > opts.AttachmentMaxBytes) is { } big)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"'{big.FileName}' is too large; files can be at most " +
                $"{AgentRuntime.Infrastructure.Documents.DocumentFormats.HumanSize(opts.AttachmentMaxBytes)}." });
        }

        var results = new List<object>();
        foreach (var file in files)
        {
            var name = TaskService.SafeFileName(file.FileName);
            // The reader works on files: a temporary copy with the same extension.
            var temp = Path.Combine(Path.GetTempPath(), $"aktor-knowledge-{Guid.NewGuid():n}{Path.GetExtension(name)}");
            try
            {
                await using (var target = System.IO.File.Create(temp))
                {
                    await file.CopyToAsync(target, ct);
                }

                var content = await AgentRuntime.Infrastructure.Documents.DocumentReader.ReadAsync(temp,
                    new AgentRuntime.Infrastructure.Documents.ReadLimits(MaxChars: MaxFileChars, MaxRowsPerSheet: 5_000), ct);
                if (string.IsNullOrWhiteSpace(content.Text))
                {
                    results.Add(new { file_name = name, entries = 0, characters = 0, truncated = false,
                        error = content.Note ?? "This file has no text to add." });
                    continue;
                }

                var chunks = Chunk(content.Text, ChunkChars);
                for (var i = 0; i < chunks.Count; i++)
                {
                    await memory.WriteAsync(new MemoryRecord
                    {
                        TenantId = access.TenantId,
                        AgentId = "user",
                        Kind = MemoryKind.Shared,
                        Key = chunks.Count == 1 ? name : $"{name} (part {i + 1} of {chunks.Count})",
                        Value = chunks[i],
                        WorkspaceId = ws
                    }, ct);
                }

                results.Add(new { file_name = name, entries = chunks.Count, characters = content.Text.Length, truncated = content.Truncated, error = (string?)null });
            }
            finally
            {
                System.IO.File.Delete(temp);
            }
        }

        return Ok(results);
    }

    /// <summary>Passages of at most <paramref name="max"/> characters, cut at blank lines where
    /// possible, then at line ends, then anywhere.</summary>
    public static List<string> Chunk(string text, int max)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            var p = paragraph.Trim();
            if (p.Length == 0) continue;
            if (current.Length > 0 && current.Length + p.Length + 2 > max)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            while (p.Length > max)
            {
                var cut = p.LastIndexOf('\n', max - 1);
                if (cut < max / 2) cut = p.LastIndexOf(' ', max - 1);
                if (cut < max / 2) cut = max;
                chunks.Add(p[..cut].Trim());
                p = p[cut..].Trim();
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(p);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }
}
