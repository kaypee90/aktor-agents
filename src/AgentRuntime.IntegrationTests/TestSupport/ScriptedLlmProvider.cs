using AgentRuntime.LLM;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>
/// Deterministic, per-scenario scriptable LLM provider (CLAUDE.md section 43): "When Root receives
/// task: return spawn_agent(...); when X receives task: return complete_task(...)". Tests set
/// <see cref="ScriptedLlmProviderRegistry.Current"/> before deploying their TestCluster. Safe only
/// because this test assembly disables cross-class parallelization (see AssemblyInfo).
/// </summary>
public static class ScriptedLlmProviderRegistry
{
    public static Func<LlmCompletionRequest, LlmCompletionResponse>? Current { get; set; }

    /// <summary>Takes precedence over <see cref="Current"/>: lets an eval pass calls to a real model.</summary>
    public static Func<LlmCompletionRequest, CancellationToken, Task<LlmCompletionResponse>>? CurrentAsync { get; set; }
}

public sealed class ScriptedLlmProvider : ILLMProvider
{
    public string ProviderName => "Scripted";

    public Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (ScriptedLlmProviderRegistry.CurrentAsync is { } live) return live(request, cancellationToken);

        var script = ScriptedLlmProviderRegistry.Current
            ?? throw new InvalidOperationException("No script registered for this test.");
        return Task.FromResult(script(request));
    }
}
