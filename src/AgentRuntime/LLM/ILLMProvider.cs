namespace AgentRuntime.LLM;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}

public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Content { get; init; }

    /// <summary>Populated on an Assistant message when the model chose to call tools.</summary>
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>Populated on a Tool message: which call this is the result of.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };
    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };
}

public sealed record LlmToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string JsonSchema { get; init; }
}

public sealed record LlmCompletionRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];
    public string? Model { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.4;
}

public enum LlmFinishReason
{
    Stop,
    ToolCalls,
    MaxTokens,
    Error
}

public sealed record LlmCompletionResponse
{
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public LlmFinishReason FinishReason { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>
/// Provider-agnostic abstraction over chat + structured tool calling (CLAUDE.md section 2/16).
/// The Agent runtime never depends on a specific provider's SDK types.
/// </summary>
public interface ILLMProvider
{
    string ProviderName { get; }

    Task<LlmCompletionResponse> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken = default);
}
