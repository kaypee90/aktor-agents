using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>
/// The contract every cloud <see cref="ILLMProvider"/> must meet for the agent runtime to work
/// unchanged on it (roadmap P10): the system prompt, conversation, tool calls and tool results map
/// onto the vendor's wire format; a tool call in the response comes back with its id, name and
/// arguments; and usage (including cached input) is reported so budgets can be enforced.
/// Each provider is exercised against a stubbed HTTP endpoint with the vendor's documented shapes.
/// </summary>
public sealed class ProviderContractTests
{
    public sealed record ProviderCase(
        string Name,
        Func<HttpClient, ILLMProvider> Create,
        string ToolCallResponse,
        string TextResponse,
        int ExpectedInput,
        int ExpectedOutput,
        int ExpectedCached)
    {
        public override string ToString() => Name;
    }

    private static IOptions<LlmOptions> Opts(string provider, string model) =>
        Options.Create(new LlmOptions { Provider = provider, Model = model, ApiKey = "test-key" });

    public static TheoryData<ProviderCase> Providers => new()
    {
        new ProviderCase("Anthropic",
            http => new AnthropicProvider(http, Opts("Anthropic", "claude-sonnet-5")),
            """
            { "content": [ { "type": "text", "text": "Spawning a researcher." },
                           { "type": "tool_use", "id": "toolu_1", "name": "spawn_agent", "input": { "role": "Researcher", "goal": "Find competitors" } } ],
              "stop_reason": "tool_use",
              "usage": { "input_tokens": 100, "cache_read_input_tokens": 900, "cache_creation_input_tokens": 0, "output_tokens": 50 } }
            """,
            """{ "content": [ { "type": "text", "text": "Done." } ], "stop_reason": "end_turn", "usage": { "input_tokens": 10, "output_tokens": 2 } }""",
            1000, 50, 900),
        new ProviderCase("OpenAI",
            http => new OpenAIProvider(http, Opts("OpenAI", "gpt-5")),
            """
            { "choices": [ { "finish_reason": "tool_calls", "message": { "role": "assistant", "content": "Spawning a researcher.",
                "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "spawn_agent", "arguments": "{\"role\":\"Researcher\",\"goal\":\"Find competitors\"}" } } ] } } ],
              "usage": { "prompt_tokens": 1000, "completion_tokens": 50, "prompt_tokens_details": { "cached_tokens": 900 } } }
            """,
            """{ "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": "Done." } } ], "usage": { "prompt_tokens": 10, "completion_tokens": 2 } }""",
            1000, 50, 900),
        new ProviderCase("Gemini",
            http => new GeminiProvider(http, Opts("Gemini", "gemini-3-pro")),
            """
            { "candidates": [ { "finishReason": "STOP", "content": { "role": "model", "parts": [
                  { "text": "Spawning a researcher." },
                  { "functionCall": { "id": "fc_1", "name": "spawn_agent", "args": { "role": "Researcher", "goal": "Find competitors" } }, "thoughtSignature": "sig-abc" } ] } } ],
              "usageMetadata": { "promptTokenCount": 1000, "cachedContentTokenCount": 900, "candidatesTokenCount": 30, "thoughtsTokenCount": 20 } }
            """,
            """{ "candidates": [ { "finishReason": "STOP", "content": { "role": "model", "parts": [ { "text": "Done." } ] } } ], "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 2 } }""",
            1000, 50, 900)
    };

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public JsonNode? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestJson = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static readonly LlmToolDefinition SpawnTool = new()
    {
        Name = "spawn_agent",
        Description = "Create an agent.",
        JsonSchema = """{ "type": "object", "properties": { "role": { "type": "string" }, "goal": { "type": "string" } }, "required": ["role", "goal"], "additionalProperties": false }"""
    };

    /// <summary>A conversation that has been through one tool round trip already.</summary>
    private static LlmCompletionRequest Conversation() => new()
    {
        Messages =
        [
            ChatMessage.System("You are the root agent."),
            ChatMessage.User("Research property management SaaS."),
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Checking who exists.",
                ToolCalls = [new ToolCall { Id = "prev_1", Name = "find_agents", ArgumentsJson = """{"capabilities":["research"]}""", ProviderSignature = "sig-prev" }]
            },
            new ChatMessage { Role = ChatRole.Tool, ToolCallId = "prev_1", ToolName = "find_agents", Content = """{"agents":[]}""" },
            ChatMessage.User("[Runtime notice] Continue.")
        ],
        Tools = [SpawnTool],
        MaxTokens = 777,
        Temperature = 0.2
    };

    private static async Task<(LlmCompletionResponse Response, StubHandler Handler)> RunAsync(ProviderCase c, string responseBody)
    {
        var handler = new StubHandler(responseBody);
        var provider = c.Create(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") });
        var response = await provider.CompleteAsync(Conversation());
        return (response, handler);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Tool_call_is_parsed_with_id_name_and_arguments(ProviderCase c)
    {
        var (response, _) = await RunAsync(c, c.ToolCallResponse);

        Assert.Equal(LlmFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("spawn_agent", call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.Id));
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("Researcher", args.RootElement.GetProperty("role").GetString());
        Assert.Equal("Find competitors", args.RootElement.GetProperty("goal").GetString());
        Assert.Equal("Spawning a researcher.", response.Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Usage_is_reported_including_cached_input(ProviderCase c)
    {
        var (response, _) = await RunAsync(c, c.ToolCallResponse);

        Assert.Equal(c.ExpectedInput, response.InputTokens);
        Assert.Equal(c.ExpectedOutput, response.OutputTokens);
        Assert.Equal(c.ExpectedCached, response.CachedInputTokens);
        // Cached tokens are a subset of input, so cost is never computed on more than was sent.
        Assert.True(response.CachedInputTokens + response.CacheWriteInputTokens <= response.InputTokens);
        var cost = new LlmOptions { Provider = c.Name }.CostOf(response);
        Assert.True(cost > 0);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Plain_text_reply_finishes_with_stop(ProviderCase c)
    {
        var (response, _) = await RunAsync(c, c.TextResponse);

        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Empty(response.ToolCalls);
        Assert.Equal("Done.", response.Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Request_carries_system_prompt_history_tool_round_trip_and_tools(ProviderCase c)
    {
        var (_, handler) = await RunAsync(c, c.TextResponse);
        var wire = handler.RequestJson!.ToJsonString();

        // Every provider must carry each of these somewhere in its own format.
        Assert.Contains("You are the root agent.", wire);
        Assert.Contains("Research property management SaaS.", wire);
        Assert.Contains("Checking who exists.", wire);
        Assert.Contains("find_agents", wire);
        Assert.Contains("prev_1", wire);                 // the tool result is tied to its call
        Assert.Contains("[Runtime notice] Continue.", wire);
        Assert.Contains("spawn_agent", wire);            // the tool catalog
        Assert.Contains("777", wire);                    // the output cap
        Assert.True(handler.Request!.Headers.Contains("x-api-key") || handler.Request.Headers.Contains("x-goog-api-key") ||
                    handler.Request.Headers.Authorization is not null, "the API key is sent");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Api_errors_surface_as_exceptions(ProviderCase c)
    {
        var handler = new ErrorHandler();
        var provider = c.Create(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(Conversation()));
        Assert.Contains("429", ex.Message);
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("""{"error":"rate limited"}""") });
    }

    // ---- Gemini specifics -------------------------------------------------------

    [Fact]
    public async Task Gemini_maps_roles_function_responses_and_thought_signatures()
    {
        var c = ((IEnumerable<object[]>)Providers).Select(p => (ProviderCase)p[0]).Single(p => p.Name == "Gemini");
        var (response, handler) = await RunAsync(c, c.ToolCallResponse);
        var body = handler.RequestJson!.AsObject();

        Assert.EndsWith("v1beta/models/gemini-3-pro:generateContent", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("You are the root agent.", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());

        var contents = body["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user", "user"], contents.Select(x => x!["role"]!.GetValue<string>()).ToArray());
        var call = contents[1]!["parts"]!.AsArray().Single(p => p!["functionCall"] is not null)!;
        Assert.Equal("sig-prev", call["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("research", call["functionCall"]!["args"]!["capabilities"]![0]!.GetValue<string>());
        var reply = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("find_agents", reply["name"]!.GetValue<string>());
        Assert.Equal(0, reply["response"]!["agents"]!.AsArray().Count);

        // Full JSON Schema goes through untouched (additionalProperties included).
        var declaration = body["tools"]![0]!["functionDeclarations"]![0]!;
        Assert.False(declaration["parametersJsonSchema"]!["additionalProperties"]!.GetValue<bool>());

        // And the signature on the new call is kept for the next request.
        Assert.Equal("sig-abc", response.ToolCalls[0].ProviderSignature);
        Assert.Equal("fc_1", response.ToolCalls[0].Id);
    }

    [Fact]
    public void Gemini_generates_an_id_when_the_api_omits_one_and_skips_thoughts()
    {
        var response = GeminiProvider.ParseResponse("""
            { "candidates": [ { "content": { "parts": [
                { "text": "private reasoning", "thought": true },
                { "functionCall": { "name": "complete_task", "args": { "status": "completed", "summary": "ok" } } } ] } } ] }
            """);

        var call = Assert.Single(response.ToolCalls);
        Assert.StartsWith("call_", call.Id);
        Assert.Null(response.Content);
    }

    [Fact]
    public void Gemini_blocked_prompt_is_an_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GeminiProvider.ParseResponse("""{ "promptFeedback": { "blockReason": "SAFETY" } }"""));
        Assert.Contains("SAFETY", ex.Message);
    }
}
