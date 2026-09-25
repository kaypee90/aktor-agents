using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using Xunit;

namespace AgentRuntime.Tests;

public class TokenEfficiencyTests
{
    private sealed class Fixed(string header, string text, bool dynamic) : ISystemPromptSection
    {
        public string Header => header;
        public bool IsDynamic => dynamic;
        public string Render(AgentPromptContext context) => text;
    }

    private static AgentPromptContext Context() => new()
    {
        State = new AgentState { AgentId = "a1", Name = "A", Role = "worker", Goal = "g" },
        AvailableTools = [],
        AutonomyLevel = AutonomyLevel.Autonomous,
        EnvironmentSummary = "env"
    };

    [Fact]
    public void PromptBuilder_PutsStableSectionsFirst_AndMarksTheCacheBoundary()
    {
        var builder = new AgentPromptBuilder([
            new Fixed("ROLE", "role text", dynamic: false),
            new Fixed("STATE", "tokens used: 123", dynamic: true),
            new Fixed("RULES", "rules text", dynamic: false)
        ]);

        var message = builder.BuildSystemPrompt(Context());
        var text = message.Content!;
        var prefix = text[..message.CacheablePrefixLength!.Value];

        Assert.True(text.IndexOf("## RULES", StringComparison.Ordinal) < text.IndexOf("## STATE", StringComparison.Ordinal));
        Assert.Contains("role text", prefix);
        Assert.Contains("rules text", prefix);
        Assert.DoesNotContain("tokens used", prefix);
    }

    [Fact]
    public void Anthropic_SetsCacheBreakpoints_OnTools_StableSystemPrefix_AndLatestMessage()
    {
        var body = AnthropicProvider.BuildBody(new LlmCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = ChatRole.System, Content = "STABLE PART|dynamic part", CacheablePrefixLength = 12 },
                ChatMessage.User("first"),
                new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall { Id = "t1", Name = "x", ArgumentsJson = "{}" }] },
                new ChatMessage { Role = ChatRole.Tool, ToolCallId = "t1", ToolName = "x", Content = "result" }
            ],
            Tools =
            [
                new LlmToolDefinition { Name = "x", Description = "d", JsonSchema = """{"type":"object"}""" },
                new LlmToolDefinition { Name = "y", Description = "d", JsonSchema = """{"type":"object"}""" }
            ]
        }, "claude-test");

        var system = body["system"]!.AsArray();
        Assert.Equal(2, system.Count);
        Assert.Equal("STABLE PART|", system[0]!["text"]!.GetValue<string>());
        Assert.NotNull(system[0]!["cache_control"]);
        Assert.Null(system[1]!["cache_control"]);

        var tools = body["tools"]!.AsArray();
        Assert.Null(tools[0]!["cache_control"]);
        Assert.NotNull(tools[^1]!["cache_control"]);

        var lastBlock = body["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!;
        Assert.Equal("tool_result", lastBlock["type"]!.GetValue<string>());
        Assert.NotNull(lastBlock["cache_control"]);
        Assert.Equal(3, CountBreakpoints(body)); // Anthropic allows at most 4
    }

    private static int CountBreakpoints(JsonNode? node) => node switch
    {
        JsonObject o => (o.ContainsKey("cache_control") ? 1 : 0) + o.Sum(kv => CountBreakpoints(kv.Value)),
        JsonArray a => a.Sum(CountBreakpoints),
        _ => 0
    };

    [Fact]
    public void Anthropic_UsageCountsCachedTokens()
    {
        var r = AnthropicProvider.ParseResponse("""
        { "content": [{"type":"text","text":"hi"}], "stop_reason": "end_turn",
          "usage": { "input_tokens": 100, "cache_read_input_tokens": 3000, "cache_creation_input_tokens": 500, "output_tokens": 50 } }
        """);

        Assert.Equal(3600, r.InputTokens);
        Assert.Equal(3000, r.CachedInputTokens);
        Assert.Equal(500, r.CacheWriteInputTokens);
    }

    [Fact]
    public void OpenAI_UsageCountsCachedTokens()
    {
        var r = OpenAIProvider.ParseResponse("""
        { "choices": [{ "message": { "role": "assistant", "content": "hi" }, "finish_reason": "stop" }],
          "usage": { "prompt_tokens": 2000, "completion_tokens": 20, "prompt_tokens_details": { "cached_tokens": 1536 } } }
        """);

        Assert.Equal(2000, r.InputTokens);
        Assert.Equal(1536, r.CachedInputTokens);
    }

    [Fact]
    public void Cost_PricesCachedAndCacheWriteTokens_AtTheirOwnRates()
    {
        var options = new LlmOptions { Provider = "Anthropic", PricePerInputTokenUsd = 0.000003m, PricePerOutputTokenUsd = 0.000015m };
        var r = new LlmCompletionResponse { InputTokens = 3600, CachedInputTokens = 3000, CacheWriteInputTokens = 500, OutputTokens = 50 };

        // 100 uncached + 3000 × 0.1 + 500 × 1.25 input-token equivalents, plus output.
        var expected = (100 + 300 + 625) * 0.000003m + 50 * 0.000015m;
        Assert.Equal(expected, options.CostOf(r));
        Assert.True(options.CostOf(r) < new LlmOptions { Provider = "Anthropic" }.CostOf(r with { CachedInputTokens = 0, CacheWriteInputTokens = 0 }));
    }

    [Fact]
    public void FastTier_UsesItsOwnModelAndPrices_OnlyWhenConfigured()
    {
        var plain = new LlmOptions { Model = "big" };
        Assert.Equal("big", plain.ModelFor(fast: true));

        var tiered = new LlmOptions { Model = "big", FastModel = "small", PricePerInputTokenUsd = 0.00001m, FastPricePerInputTokenUsd = 0.000001m, PricePerOutputTokenUsd = 0, FastPricePerOutputTokenUsd = 0 };
        var r = new LlmCompletionResponse { InputTokens = 1000 };
        Assert.Equal("small", tiered.ModelFor(fast: true));
        Assert.Equal("big", tiered.ModelFor(fast: false));
        Assert.Equal(0.001m, tiered.CostOf(r, fast: true));
        Assert.Equal(0.01m, tiered.CostOf(r, fast: false));
    }

    [Theory]
    [InlineData(true, false, "Monitor", true)]      // resident
    [InlineData(false, true, "Monitor", true)]      // standing helper
    [InlineData(false, true, "Coordinator", false)] // plans: main model
    [InlineData(false, false, "Worker", false)]     // real work: main model
    public void FastTier_IsForRoutineEventHandling(bool resident, bool standing, string role, bool expected) =>
        Assert.Equal(expected, new AgentState { WorldId = resident ? "world-1" : null, Standing = standing, Role = role }.UsesFastTier);
}
