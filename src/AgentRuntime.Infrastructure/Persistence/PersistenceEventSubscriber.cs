using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Makes Postgres the durable source of truth (CLAUDE.md section 39) by tailing the in-process
/// event bus and writing rows. Orleans grain state stays fast/ephemeral; this is history.
/// </summary>
public sealed class PersistenceEventSubscriber(
    IEventStream eventStream,
    IServiceScopeFactory scopeFactory,
    ILogger<PersistenceEventSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var evt in eventStream.Subscribe(stoppingToken))
            {
                try
                {
                    await HandleAsync(evt, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist event {EventType} {EventId}", evt.Type, evt.EventId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task HandleAsync(RuntimeEvent evt, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        db.Events.Add(new EventRecord
        {
            EventId = evt.EventId,
            Type = evt.Type.ToString(),
            Timestamp = evt.Timestamp,
            AgentId = evt.AgentId,
            ParentAgentId = evt.ParentAgentId,
            TargetAgentId = evt.TargetAgentId,
            TaskId = evt.TaskId,
            CorrelationId = evt.CorrelationId,
            Summary = evt.Summary,
            DataJson = JsonSerializer.Serialize(evt.Data)
        });

        switch (evt.Type)
        {
            case RuntimeEventType.TaskCreated when evt.TaskId is not null:
                db.Tasks.Add(new TaskRecord
                {
                    TaskId = evt.TaskId,
                    Goal = evt.Data.GetValueOrDefault("goal", evt.Summary),
                    RootAgentId = evt.Data.GetValueOrDefault("rootAgentId", evt.AgentId),
                    CreatedAt = evt.Timestamp
                });
                break;

            case RuntimeEventType.AgentCreated or RuntimeEventType.AgentSpawned or RuntimeEventType.AgentStarted
                or RuntimeEventType.AgentStatusChanged or RuntimeEventType.AgentCompleted
                or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated:
                await UpsertAgentAsync(scope, db, evt.Type == RuntimeEventType.AgentSpawned ? evt.TargetAgentId : evt.AgentId, ct);
                if (evt.Type is RuntimeEventType.AgentCompleted or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated)
                {
                    await MaybeCompleteTaskAsync(scope, db, evt, ct);
                }
                break;

            case RuntimeEventType.AgentMessageSent:
                // Message ids are deterministic for replayed sends (docs/durability.md), so the
                // same message can be reported twice after a crash; record it once.
                var messageId = evt.Data.GetValueOrDefault("messageId", Guid.NewGuid().ToString("n"));
                if (await db.Messages.AnyAsync(m => m.MessageId == messageId, ct)) break;
                db.Messages.Add(new MessageRecord
                {
                    MessageId = messageId,
                    FromAgentId = evt.AgentId ?? string.Empty,
                    ToAgentId = evt.TargetAgentId ?? string.Empty,
                    ConversationId = evt.Data.GetValueOrDefault("conversationId", string.Empty),
                    CorrelationId = evt.CorrelationId,
                    MessageType = evt.Data.GetValueOrDefault("messageType", "TaskRequest"),
                    Timestamp = evt.Timestamp,
                    Payload = evt.Data.GetValueOrDefault("payload", string.Empty),
                    TaskId = evt.TaskId ?? string.Empty
                });
                break;

            case RuntimeEventType.AgentToolCompleted:
                db.ToolCalls.Add(new ToolCallRecord
                {
                    AgentId = evt.AgentId ?? string.Empty,
                    TaskId = evt.TaskId ?? string.Empty,
                    ToolName = evt.Data.GetValueOrDefault("tool", "unknown"),
                    ArgumentsJson = evt.Data.GetValueOrDefault("arguments", "{}"),
                    ResultJson = evt.Data.GetValueOrDefault("result"),
                    Success = evt.Data.GetValueOrDefault("success") == "True",
                    Timestamp = evt.Timestamp
                });
                break;

            case RuntimeEventType.ArtifactCreated:
                db.Artifacts.Add(new ArtifactRecord
                {
                    ArtifactId = evt.Data.GetValueOrDefault("artifactId", Guid.NewGuid().ToString("n")),
                    Type = evt.Data.GetValueOrDefault("type", "Document"),
                    Location = evt.Data.GetValueOrDefault("location", string.Empty),
                    CreatedByAgent = evt.AgentId ?? string.Empty,
                    TaskId = evt.TaskId ?? string.Empty,
                    CreatedAt = evt.Timestamp,
                    MetadataJson = JsonSerializer.Serialize(evt.Data)
                });
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task UpsertAgentAsync(IServiceScope scope, AgentDbContext db, string? agentId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId)) return;

        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        var snapshot = await orchestrator.GetSnapshotAsync(agentId, ct);
        if (snapshot is null) return;

        var existing = await db.Agents.FindAsync([agentId], ct);
        if (existing is null)
        {
            db.Agents.Add(new AgentRecord
            {
                AgentId = snapshot.AgentId,
                ParentAgentId = snapshot.ParentAgentId,
                RootAgentId = snapshot.RootAgentId,
                TaskId = snapshot.TaskId,
                Name = snapshot.Name,
                Role = snapshot.Role,
                Goal = snapshot.Goal,
                Status = snapshot.Status.ToString(),
                CapabilitiesJson = JsonSerializer.Serialize(snapshot.Capabilities),
                AllowedToolsJson = JsonSerializer.Serialize(snapshot.AllowedTools),
                Depth = snapshot.Depth,
                CreatedAt = snapshot.CreatedAt,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                TokensUsed = snapshot.Usage.TokensUsed,
                ToolCallsUsed = snapshot.Usage.ToolCallsUsed,
                ChildrenSpawned = snapshot.Usage.ChildrenSpawned,
                CostUsd = snapshot.Usage.CostUsd,
                FailureReason = snapshot.FailureReason
            });
        }
        else
        {
            existing.Status = snapshot.Status.ToString();
            existing.StartedAt = snapshot.StartedAt;
            existing.CompletedAt = snapshot.CompletedAt;
            existing.TokensUsed = snapshot.Usage.TokensUsed;
            existing.ToolCallsUsed = snapshot.Usage.ToolCallsUsed;
            existing.ChildrenSpawned = snapshot.Usage.ChildrenSpawned;
            existing.CostUsd = snapshot.Usage.CostUsd;
            existing.FailureReason = snapshot.FailureReason;
        }
    }

    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static async Task MaybeCompleteTaskAsync(IServiceScope scope, AgentDbContext db, RuntimeEvent evt, CancellationToken ct)
    {
        if (evt.TaskId is null) return;

        // Only the root agent completing/failing/being terminated closes the whole task.
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        var snapshot = await orchestrator.GetSnapshotAsync(evt.AgentId ?? string.Empty, ct);
        if (snapshot is null || snapshot.AgentId != snapshot.RootAgentId) return;

        var task = await db.Tasks.FindAsync([evt.TaskId], ct);
        if (task is null) return;

        var summary = evt.Data.GetValueOrDefault("summary", evt.Summary);
        task.Status = snapshot.Status.ToString();
        task.CompletedAt = snapshot.CompletedAt ?? DateTimeOffset.UtcNow;
        task.ResultSummary = summary;

        // Aggregate the full TaskResult (CLAUDE.md section 52) from every agent that worked on
        // this task, not just the root's own summary — this is what GET /api/tasks/{id}/result
        // and the dashboard's "Final Result" panel read.
        var participatingAgents = await db.Agents.CountAsync(a => a.TaskId == evt.TaskId, ct);

        var artifacts = await db.Artifacts
            .Where(a => a.TaskId == evt.TaskId)
            .Select(a => new { a.ArtifactId, a.Type, a.Location, a.CreatedByAgent })
            .ToListAsync(ct);

        var completionEvents = await db.Events
            .Where(e => e.TaskId == evt.TaskId && e.Type == nameof(RuntimeEventType.AgentCompleted))
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var findings = new List<string>();
        foreach (var e in completionEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                if (doc.RootElement.TryGetProperty("summary", out var summaryProp) && e.AgentId != evt.AgentId)
                {
                    findings.Add($"[{e.AgentId}] {summaryProp.GetString()}");
                }
            }
            catch (JsonException)
            {
                // Older/malformed event payload; skip rather than fail the whole aggregation.
            }
        }

        var unresolvedItems = new List<string>();
        if (evt.Data.TryGetValue("remaining_work", out var remainingWorkJson))
        {
            try { unresolvedItems = JsonSerializer.Deserialize<List<string>>(remainingWorkJson) ?? []; }
            catch (JsonException) { /* ignore */ }
        }

        var totalToolCalls = await db.ToolCalls.CountAsync(t => t.TaskId == evt.TaskId, ct);
        var totalTokens = await db.Agents.Where(a => a.TaskId == evt.TaskId).SumAsync(a => a.TokensUsed, ct);
        var totalCost = await db.Agents.Where(a => a.TaskId == evt.TaskId).SumAsync(a => a.CostUsd, ct);

        var result = new TaskResult
        {
            Status = evt.Data.GetValueOrDefault("status", snapshot.Status.ToString()),
            Summary = summary,
            Findings = findings,
            Artifacts = artifacts.Select(a => $"{a.ArtifactId}:{a.Type}:{a.Location}").ToList(),
            ParticipatingAgents = participatingAgents,
            UnresolvedItems = unresolvedItems,
            Metrics = new Dictionary<string, string>
            {
                ["total_tool_calls"] = totalToolCalls.ToString(),
                ["total_tokens_used"] = totalTokens.ToString(),
                ["total_cost_usd"] = totalCost.ToString("F4")
            }
        };

        task.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);
    }
}
