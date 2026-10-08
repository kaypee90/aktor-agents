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
    Tasks.TaskCompletionNotifier completions,
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

    private static int IntOf(RuntimeEvent evt, string key) =>
        long.TryParse(evt.Data.GetValueOrDefault(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Clamp(v, int.MinValue, int.MaxValue) : 0;

    private async Task HandleAsync(RuntimeEvent evt, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var tenant = AgentRuntime.Tenancy.TenantIds.Normalize(evt.TenantId);
        string? completedTaskId = null;

        db.Events.Add(new EventRecord
        {
            TenantId = tenant,
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
                // The task service writes the row before starting the root agent (so callers can
                // poll it straight away); tasks started any other way get their row here.
                var existingTask = await db.Tasks.FindAsync([evt.TaskId], ct);
                if (existingTask is null)
                {
                    db.Tasks.Add(new TaskRecord
                    {
                        TenantId = tenant,
                        TaskId = evt.TaskId,
                        Goal = evt.Data.TryGetValue("goal", out var goal) ? goal : evt.Summary,
                        RootAgentId = evt.Data.TryGetValue("rootAgentId", out var rootId) ? rootId : evt.AgentId,
                        CorrelationId = evt.CorrelationId,
                        CreatedAt = evt.Timestamp,
                        // Pipeline runs say where they come from and who started them.
                        Source = evt.Data.GetValueOrDefault("source") is { Length: > 0 } source ? source : "api",
                        Status = evt.Data.GetValueOrDefault("status") is { Length: > 0 } status ? status : "Running",
                        WorkspaceId = evt.Data.GetValueOrDefault("workspace_id") is { Length: > 0 } workspaceId ? workspaceId : null,
                        StartedBy = evt.Data.GetValueOrDefault("started_by") is { Length: > 0 } startedBy ? startedBy : null
                    });
                }
                else
                {
                    existingTask.RootAgentId ??= evt.Data.TryGetValue("rootAgentId", out var existingRoot) ? existingRoot : evt.AgentId;
                }
                break;

            case RuntimeEventType.AgentCreated or RuntimeEventType.AgentSpawned or RuntimeEventType.AgentStarted
                or RuntimeEventType.AgentStatusChanged or RuntimeEventType.AgentCompleted
                or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated:
                await UpsertAgentAsync(scope, db, evt.Type == RuntimeEventType.AgentSpawned ? evt.TargetAgentId : evt.AgentId, ct);
                if ((evt.Type is RuntimeEventType.AgentCompleted or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated) &&
                    await MaybeCompleteTaskAsync(scope, db, evt, ct))
                {
                    completedTaskId = evt.TaskId;
                }
                break;

            case RuntimeEventType.TaskReopened when evt.TaskId is not null:
                // The root took up a follow-up: the task runs again until the root reports. The
                // earlier answer stays in the task's chat (its completion event); the result is
                // rebuilt when the root next finishes.
                if (await db.Tasks.FindAsync([evt.TaskId], ct) is { } reopenedTask)
                {
                    reopenedTask.Status = "Running";
                    reopenedTask.CompletedAt = null;
                    reopenedTask.ResultJson = null;
                    // A completion webhook goes out again for this round's result.
                    reopenedTask.CallbackDeliveredAt = null;
                    reopenedTask.CallbackAttempts = 0;
                }
                await UpsertAgentAsync(scope, db, evt.AgentId, ct);
                break;

            case RuntimeEventType.AgentMessageSent:
                // Message ids are deterministic for replayed sends (docs/durability.md), so the
                // same message can be reported twice after a crash; record it once.
                var messageId = evt.Data.GetValueOrDefault("messageId", Guid.NewGuid().ToString("n"));
                if (await db.Messages.AnyAsync(m => m.MessageId == messageId, ct)) break;
                db.Messages.Add(new MessageRecord
                {
                    TenantId = tenant,
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
                    TenantId = tenant,
                    AgentId = evt.AgentId ?? string.Empty,
                    TaskId = evt.TaskId ?? string.Empty,
                    ToolName = evt.Data.GetValueOrDefault("tool", "unknown"),
                    ArgumentsJson = evt.Data.GetValueOrDefault("arguments", "{}"),
                    ResultJson = evt.Data.GetValueOrDefault("result"),
                    Success = evt.Data.GetValueOrDefault("success") == "True",
                    DurationMs = int.TryParse(evt.Data.GetValueOrDefault("duration_ms"), out var toolMs) ? toolMs : null,
                    Timestamp = evt.Timestamp
                });
                break;

            case RuntimeEventType.LlmCallCompleted:
                db.LlmCalls.Add(new LlmCallRecord
                {
                    TenantId = tenant,
                    TaskId = evt.TaskId ?? string.Empty,
                    WorkspaceId = evt.Data.GetValueOrDefault("workspace_id") is { Length: > 0 } ws ? ws : null,
                    AgentId = evt.AgentId ?? string.Empty,
                    Role = evt.Data.GetValueOrDefault("role", string.Empty),
                    ProfileId = evt.Data.GetValueOrDefault("profile_id", "server"),
                    ProfileName = evt.Data.GetValueOrDefault("profile_name", string.Empty),
                    Provider = evt.Data.GetValueOrDefault("provider", string.Empty),
                    Model = evt.Data.GetValueOrDefault("model", string.Empty),
                    Purpose = evt.Data.GetValueOrDefault("purpose", "step"),
                    InputTokens = IntOf(evt, "input_tokens"),
                    OutputTokens = IntOf(evt, "output_tokens"),
                    CachedInputTokens = IntOf(evt, "cached_input_tokens"),
                    CacheWriteInputTokens = IntOf(evt, "cache_write_input_tokens"),
                    CostUsd = decimal.TryParse(evt.Data.GetValueOrDefault("cost_usd"), System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var callCost) ? callCost : 0,
                    DurationMs = IntOf(evt, "duration_ms"),
                    Timestamp = evt.Timestamp
                });
                break;

            case RuntimeEventType.ArtifactCreated:
                // Attachments are recorded by the API before their event, so a follow-up can use them at once.
                if (await db.Artifacts.AnyAsync(a => a.ArtifactId == evt.Data.GetValueOrDefault("artifactId", string.Empty), ct)) break;
                db.Artifacts.Add(new ArtifactRecord
                {
                    TenantId = tenant,
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

        // After the save: whoever is woken reads the finished row.
        if (completedTaskId is not null) completions.Signal(completedTaskId);
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
                TenantId = AgentRuntime.Tenancy.TenantIds.Normalize(snapshot.TenantId),
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
                FailureReason = snapshot.FailureReason,
                ModelProfileId = snapshot.ModelProfileId
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

    /// <summary>Estimate vs actual (roadmap P2), for tasks started from a preview. Later previews
    /// calibrate on these numbers.</summary>
    private static void AddEstimateMetrics(TaskRecord task, Dictionary<string, string> metrics, int agents, long tokens, decimal cost)
    {
        if (task.EstimateJson is null) return;
        try
        {
            using var doc = JsonDocument.Parse(task.EstimateJson);
            var e = doc.RootElement;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var estCost = e.GetProperty("cost_usd_expected").GetDecimal();
            var estTokens = e.GetProperty("tokens_expected").GetInt64();
            metrics["estimated_cost_usd"] = estCost.ToString("F4", inv);
            metrics["estimated_cost_usd_range"] = $"{e.GetProperty("cost_usd_low").GetDecimal().ToString("F4", inv)}-{e.GetProperty("cost_usd_high").GetDecimal().ToString("F4", inv)}";
            metrics["estimated_tokens"] = estTokens.ToString(inv);
            metrics["estimated_team_size"] = e.GetProperty("team_size").GetInt32().ToString(inv);
            metrics["actual_team_size"] = agents.ToString(inv);
            if (estCost > 0) metrics["cost_estimate_ratio"] = (cost / estCost).ToString("F2", inv);
            if (estTokens > 0) metrics["token_estimate_ratio"] = ((double)tokens / estTokens).ToString("F2", inv);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // An estimate saved in an older shape: report the result without the comparison.
        }
    }

    /// <summary>Closes the task when its root agent finishes. True if it did.</summary>
    private static async Task<bool> MaybeCompleteTaskAsync(IServiceScope scope, AgentDbContext db, RuntimeEvent evt, CancellationToken ct)
    {
        if (evt.TaskId is null) return false;

        // Only the root agent completing/failing/being terminated closes the whole task.
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        var snapshot = await orchestrator.GetSnapshotAsync(evt.AgentId ?? string.Empty, ct);
        if (snapshot is null || snapshot.AgentId != snapshot.RootAgentId) return false;

        var task = await db.Tasks.FindAsync([evt.TaskId], ct);
        if (task is null) return false;

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
                ["total_cost_usd"] = totalCost.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        AddEstimateMetrics(task, result.Metrics, participatingAgents, totalTokens, totalCost);

        task.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);
        return true;
    }
}
