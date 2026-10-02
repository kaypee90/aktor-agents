using AgentRuntime.Contracts;

namespace AgentRuntime.LLM;

/// <summary>Everything a prompt section needs to render itself. See CLAUDE.md section 36.</summary>
public sealed record AgentPromptContext
{
    public required AgentState State { get; init; }
    public required IReadOnlyList<ToolDefinitionSummary> AvailableTools { get; init; }
    public required AutonomyLevel AutonomyLevel { get; init; }
    public required string EnvironmentSummary { get; init; }
    /// <summary>The organization's enabled skills (name and description only).</summary>
    public IReadOnlyList<Skills.SkillSummary> Skills { get; init; } = [];
    /// <summary>The organization's models an agent may give the agents it spawns (empty when it can't).</summary>
    public IReadOnlyList<ModelOption> Models { get; init; } = [];
    /// <summary>The model this agent runs on now.</summary>
    public string? CurrentModel { get; init; }
}

/// <summary>A model an agent can pick for an agent it spawns, as listed in its prompt.</summary>
public sealed record ModelOption(string Id, string Name, string? Description, string Provider, string Model, decimal InPerMillion, decimal OutPerMillion);

public sealed record ToolDefinitionSummary(string Name, string Description);

/// <summary>One composable section of the system prompt (ROLE, GOAL, RESOURCE LIMITS, ...).</summary>
public interface ISystemPromptSection
{
    string Header { get; }
    string Render(AgentPromptContext context);

    /// <summary>True for sections that change between calls (status, usage, time). The builder puts
    /// them after all stable sections, so the stable prefix can be served from the provider's
    /// prompt cache on every call.</summary>
    bool IsDynamic => false;
}

/// <summary>
/// Builds the agent system prompt from independent sections instead of one hard-coded string
/// (CLAUDE.md section 36), so individual sections can be swapped/tested/extended in isolation.
/// </summary>
public interface IAgentPromptBuilder
{
    ChatMessage BuildSystemPrompt(AgentPromptContext context);

    /// <summary>The single planning call behind a task preview (roadmap P2): sketch the team a root
    /// agent would build for <paramref name="goal"/>, within the runtime's limits.</summary>
    LlmCompletionRequest BuildTeamPreviewRequest(string goal, Contracts.ResourceBudget budget,
        Configuration.RuntimeLimitsOptions limits, string? model, int maxOutputTokens) =>
        Resources.TeamPreviewPrompt.Build(goal, budget, limits, model, maxOutputTokens);
}
