using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The organization's shared knowledge (docs/memory.md): what agents saved with
/// write_memory(shared=true), searchable by meaning and words, plus facts people add for them.
/// </summary>
[ApiController]
[Route("api/memory")]
public sealed class MemoryController(IMemoryStore memory, IEmbeddingProvider embeddings, TenantAccess access) : ControllerBase
{
    public sealed record AddKnowledgeBody(string Key, string Value);

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
        var results = await memory.SearchAsync(access.TenantId, q ?? string.Empty, MemoryKind.Shared, cancellationToken: ct);
        return Ok(results.Take(Math.Clamp(limit, 1, 200)).Select(r => new
        {
            memory_id = r.MemoryId,
            key = r.Key,
            value = r.Value,
            agent_id = r.AgentId,
            created_at = r.CreatedAt,
            score = r.Score
        }));
    }

    /// <summary>Adds a fact every agent of the organization can find with search_knowledge.</summary>
    [HttpPost]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Add([FromBody] AddKnowledgeBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Key) || string.IsNullOrWhiteSpace(body.Value)) return BadRequest(new { error = "key and value are required" });
        if (body.Key.Length > 200 || body.Value.Length > 20_000) return BadRequest(new { error = "key is at most 200 characters, value at most 20,000" });
        await memory.WriteAsync(new MemoryRecord
        {
            TenantId = access.TenantId,
            AgentId = "user",
            Kind = MemoryKind.Shared,
            Key = body.Key.Trim(),
            Value = body.Value.Trim()
        }, ct);
        return NoContent();
    }
}
