using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.LLM;
using AgentRuntime.Tools;

namespace AgentRuntime.Durability;

/// <summary>
/// One recorded step of a run (roadmap P6). LLM steps hold the model's full decision; tool steps
/// hold a call's result. Steps are keyed by the agent's position in the tree
/// (<see cref="AgentPath"/>), not its id, because a replay's agents get new ids.
/// </summary>
public sealed record JournalStep
{
    public string TenantId { get; init; } = Tenancy.TenantIds.Default;
    public required string TaskId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentPath { get; init; }
    /// <summary><see cref="LlmKind"/> or <see cref="ToolKind"/>.</summary>
    public required string Kind { get; init; }
    /// <summary>The step number for LLM steps, the tool call id for tool steps.</summary>
    public required string Key { get; init; }
    public int Step { get; init; }
    public string? ToolName { get; init; }
    public string PayloadJson { get; init; } = "{}";
    /// <summary>LLM steps: messages and events the agent had received when it made the call. A replay
    /// serves the step only once the replaying agent has received as many, so decisions are made after
    /// the same inputs as originally (e.g. a parent's summary after its children reported).</summary>
    public int InputsReceived { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Order across the whole run, set by the journal. "Fork after step N" means this number.</summary>
    public long Seq { get; init; }

    public const string LlmKind = "llm";
    public const string ToolKind = "tool";
}

/// <summary>The durable record of every decision and tool result, for replay, audit and diffing runs.</summary>
public interface IStepJournal
{
    /// <summary>Records a step. Recording the same step again (a step re-run after a crash) replaces it.</summary>
    Task RecordAsync(JournalStep step, CancellationToken cancellationToken = default);

    Task<JournalStep?> GetAsync(string taskId, string agentPath, string kind, string key, CancellationToken cancellationToken = default);

    /// <summary>Every step of a run, in order.</summary>
    Task<IReadOnlyList<JournalStep>> ListAsync(string taskId, CancellationToken cancellationToken = default);
}

/// <summary>In-process journal (tests, Memory mode). The infrastructure layer makes it durable.</summary>
public sealed class InMemoryStepJournal : IStepJournal
{
    private readonly ConcurrentDictionary<(string, string, string, string), JournalStep> _steps = new();
    private long _seq;

    public Task RecordAsync(JournalStep step, CancellationToken cancellationToken = default)
    {
        var key = (step.TaskId, step.AgentPath, step.Kind, step.Key);
        _steps[key] = step with { Seq = Interlocked.Increment(ref _seq) };
        return Task.CompletedTask;
    }

    public Task<JournalStep?> GetAsync(string taskId, string agentPath, string kind, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_steps.GetValueOrDefault((taskId, agentPath, kind, key)));

    public Task<IReadOnlyList<JournalStep>> ListAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JournalStep>>(_steps.Values.Where(s => s.TaskId == taskId).OrderBy(s => s.Seq).ToList());
}

public enum ReplayMode
{
    /// <summary>Every decision and external tool result comes from the journal; nothing calls a
    /// model or reaches outside. Should reproduce the original run.</summary>
    Full,
    /// <summary>Recorded up to a step, then live: what would have happened from there?</summary>
    Fork
}

/// <summary>Set on a replay's root agent and inherited by every agent it spawns.</summary>
[GenerateSerializer]
public sealed record ReplaySpec
{
    [Id(0)] public required string SourceTaskId { get; init; }
    [Id(1)] public ReplayMode Mode { get; init; }
    /// <summary>Fork: steps with a journal sequence number above this run live.</summary>
    [Id(2)] public long? ForkAfterSeq { get; init; }
}

/// <summary>
/// Which tool calls a replay runs again, and which it answers from the journal. Runtime actions
/// (spawning, messaging, completing, discovery) and the task's own sandboxed files are re-run, so
/// the replay rebuilds the same agent tree, messages and artifacts; everything that reaches outside
/// the platform (web, shell, HTTP, databases, connections, notifications) or writes shared memory is
/// answered from the journal, so a replay never causes an external side effect.
/// </summary>
public static class ReplayPolicy
{
    private static readonly HashSet<string> Rerun = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn_agent", "send_message", "find_agents", "get_agent_status", "list_children", "complete_task",
        "read_memory", "plan_request", "wait_for_events", "end_turn",
        "filesystem_read", "filesystem_write", "filesystem_list", "create_document"
    };

    public static bool ReRuns(string toolName) => Rerun.Contains(toolName);

    public static string ToolPayload(ToolExecutionResult result) => JsonSerializer.Serialize(new ToolPayloadDto(
        result.Success, result.ResultJson, result.ErrorMessage, result.ErrorCode, result.ErrorDetailsJson));

    public static ToolExecutionResult ToolResult(string payloadJson)
    {
        var p = JsonSerializer.Deserialize<ToolPayloadDto>(payloadJson)!;
        return p.Success
            ? ToolExecutionResult.Ok(p.ResultJson ?? "{}")
            : new ToolExecutionResult { Success = false, ErrorMessage = p.Error, ErrorCode = p.ErrorCode, ErrorDetailsJson = p.ErrorDetailsJson };
    }

    public static string LlmPayload(LlmCompletionResponse response) => JsonSerializer.Serialize(new LlmPayloadDto(
        response.Content,
        response.ToolCalls.Select(c => new ToolCallDto(c.Id, c.Name, c.ArgumentsJson, c.ProviderSignature)).ToList(),
        response.FinishReason, response.InputTokens, response.OutputTokens, response.CachedInputTokens, response.CacheWriteInputTokens));

    public static LlmCompletionResponse LlmResponse(string payloadJson)
    {
        var p = JsonSerializer.Deserialize<LlmPayloadDto>(payloadJson)!;
        return new LlmCompletionResponse
        {
            Content = p.Content,
            ToolCalls = p.ToolCalls.Select(c => new ToolCall { Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson, ProviderSignature = c.Signature }).ToList(),
            FinishReason = p.FinishReason,
            InputTokens = p.InputTokens,
            OutputTokens = p.OutputTokens,
            CachedInputTokens = p.CachedInputTokens,
            CacheWriteInputTokens = p.CacheWriteInputTokens
        };
    }

    private sealed record ToolPayloadDto(bool Success, string? ResultJson, string? Error, string? ErrorCode, string? ErrorDetailsJson);

    private sealed record ToolCallDto(string Id, string Name, string ArgumentsJson, string? Signature);

    private sealed record LlmPayloadDto(string? Content, List<ToolCallDto> ToolCalls, LlmFinishReason FinishReason,
        int InputTokens, int OutputTokens, int CachedInputTokens, int CacheWriteInputTokens);
}

/// <summary>
/// An <see cref="ILLMProvider"/> that answers from a past run's journal instead of a model (roadmap
/// P6), keyed by the agent's tree path and step number carried on the request. It never calls out:
/// a step that wasn't recorded is an error, which the runtime turns into "replay exhausted" (full
/// replay) or a switch to the live model (fork).
/// </summary>
public sealed class RecordedLlmProvider(IStepJournal journal) : ILLMProvider
{
    public string ProviderName => "Recorded";

    public async Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var replay = request.Replay ?? throw new InvalidOperationException("A recorded response needs the step being replayed.");
        var step = await journal.GetAsync(replay.SourceTaskId, replay.AgentPath, JournalStep.LlmKind, replay.Step.ToString(), cancellationToken)
                   ?? throw new InvalidOperationException($"No recorded step {replay.Step} for agent path '{replay.AgentPath}' in task {replay.SourceTaskId}.");
        return ReplayPolicy.LlmResponse(step.PayloadJson);
    }

    public Task<JournalStep?> GetStepAsync(string sourceTaskId, string agentPath, int step, CancellationToken cancellationToken = default) =>
        journal.GetAsync(sourceTaskId, agentPath, JournalStep.LlmKind, step.ToString(), cancellationToken);
}
