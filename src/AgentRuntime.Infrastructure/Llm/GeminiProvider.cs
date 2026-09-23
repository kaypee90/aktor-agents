using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// Placeholder proving the provider abstraction scales to a third vendor without touching
/// AgentRuntime (CLAUDE.md section 2). Wire up Gemini's generateContent + function-calling API
/// here following the same pattern as <see cref="AnthropicProvider"/> / <see cref="OpenAIProvider"/>
/// to make this provider selectable via configuration.
/// </summary>
public sealed class GeminiProvider : ILLMProvider
{
    public string ProviderName => "Gemini";

    public Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "GeminiProvider is a placeholder. Implement Google's generateContent + function-calling API here, " +
            "matching the ILLMProvider contract used by AnthropicProvider and OpenAIProvider.");
}
