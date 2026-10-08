namespace AgentRuntime.Tools;

/// <summary>
/// Sees every tool result before the agent does, and may attach an evidence id to it (docs/studies.md:
/// knowledge searches and connection calls inside a study become citable evidence). The default
/// returns results unchanged.
/// </summary>
public interface IToolEvidenceRecorder
{
    Task<ToolExecutionResult> AttachAsync(ToolExecutionRequest request, ToolExecutionResult result);
}

public sealed class NoToolEvidence : IToolEvidenceRecorder
{
    public Task<ToolExecutionResult> AttachAsync(ToolExecutionRequest request, ToolExecutionResult result) => Task.FromResult(result);
}

/// <summary>A runtime rule an agent's complete_task must pass, e.g. a study run's report must be
/// accepted first. Returns why completion is refused, or null.</summary>
public interface ICompletionGate
{
    Task<string?> CheckAsync(ToolExecutionRequest request);
}
