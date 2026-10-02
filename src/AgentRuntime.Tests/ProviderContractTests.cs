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
            http => new OpenAIProvider(http, Opts("OpenAI", "gpt-4o")),
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

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!);
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
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

    // ---- OpenAI specifics -------------------------------------------------------

    [Theory]
    // gpt-4o and gpt-4 keep the legacy shape and their sampling; every reasoning-era generation
    // needs the new output cap and refuses temperature/top_p outright.
    [InlineData("gpt-4o", false, true)]
    [InlineData("gpt-4-turbo", false, true)]
    [InlineData("gpt-3.5-turbo", false, true)]
    [InlineData("gpt-6-astra", true, false)]
    [InlineData("gpt-6.1-sol", true, false)]
    [InlineData("gpt-5", true, false)]
    [InlineData("gpt-5.6-terra", true, false)]
    [InlineData("o3", true, false)]
    [InlineData("o4-mini-2025-04-16", true, false)]
    [InlineData("codex-mini-latest", true, false)]
    // Gateways prefix the vendor id; the family is still readable.
    [InlineData("openai/gpt-6-astra", true, false)]
    [InlineData("azure/o3", true, false)]
    public void OpenAI_Detects_which_request_shape_a_model_speaks(string model, bool useMaxCompletionTokens, bool supportsSampling)
    {
        var detected = OpenAiCapabilities.Detect(model);

        Assert.Equal(useMaxCompletionTokens, detected.UseMaxCompletionTokens);
        Assert.Equal(supportsSampling, detected.SupportsSampling);
    }

    [Fact]
    public async Task OpenAI_Reasoning_model_gets_max_completion_tokens_and_no_temperature()
    {
        // gpt-5: a reasoning model that still serves tools on Chat Completions, so this is the
        // shape there. gpt-6 needs the Responses endpoint and is covered separately below.
        var (_, handler) = await RunOpenAiAsync("gpt-5", 4096);
        var body = handler.RequestJson!.AsObject();

        // Reasoning tokens come out of the same allowance, so a bare 4096 would be spent before
        // a visible token was written; the floor keeps the answer reachable.
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.True(body["max_completion_tokens"]!.GetValue<int>() >= 25_000);
        Assert.False(body.ContainsKey("temperature"));
    }

    [Fact]
    public async Task OpenAI_Legacy_model_keeps_max_tokens_and_its_temperature()
    {
        var (_, handler) = await RunOpenAiAsync("gpt-4o", 4096);
        var body = handler.RequestJson!.AsObject();

        Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task OpenAI_Reasoning_floor_never_lowers_a_larger_requested_cap()
    {
        var (_, handler) = await RunOpenAiAsync("gpt-5", 64_000);

        Assert.Equal(64_000, handler.RequestJson!["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task OpenAI_Responds_to_a_rejected_parameter_instead_of_failing_the_call()
    {
        // An unrecognised model id: the guess is wrong, and the provider has to notice from the
        // API's own error rather than take the agent's task down with it.
        var handler = new RejectOnceHandler(
            """
            { "error": { "message": "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.",
              "type": "invalid_request_error", "param": "max_tokens", "code": "unsupported_parameter" } }
            """,
            """{ "choices": [{ "finish_reason": "stop", "message": { "role": "assistant", "content": "Done." } }] }""");

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "some-future-model"));
        var response = await provider.CompleteAsync(Conversation());

        Assert.Equal("Done.", response.Content);
        Assert.Equal(2, handler.RequestCount);
        Assert.False(handler.Bodies[0]!.AsObject().ContainsKey("max_completion_tokens"));
        // Having learned the model reasons, the retry also gets the reasoning floor, not just
        // the renamed field: the cap covers reasoning tokens, which a bare 777 can't hold.
        Assert.Equal(25_000, handler.Bodies[1]!["max_completion_tokens"]!.GetValue<int>());

        // And it remembers: the correction is paid once per model, not once per call.
        await provider.CompleteAsync(Conversation());
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(25_000, handler.Bodies[2]!["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task OpenAI_Drops_a_temperature_a_reasoning_model_refuses()
    {
        // o1-era wording: an unsupported *value* rather than an unsupported parameter.
        var handler = new RejectOnceHandler(
            """
            { "error": { "message": "Unsupported value: 'temperature' does not support 0.2 with this model. Only the default (1) is supported.",
              "type": "invalid_request_error", "param": "temperature", "code": "unsupported_value" } }
            """,
            """{ "choices": [{ "finish_reason": "stop", "message": { "role": "assistant", "content": "Done." } }] }""");

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "another-future-model"));
        await provider.CompleteAsync(Conversation());

        Assert.Equal(2, handler.RequestCount);
        Assert.False(handler.Bodies[1]!.AsObject().ContainsKey("temperature"));
    }

    [Theory]
    // Nothing about these says "wrong parameter", so re-sending would only bury a real error
    // (bad key, rate limit, malformed request) under a second, more confusing one.
    [InlineData("""{ "error": { "message": "Incorrect API key provided.", "type": "invalid_request_error", "code": "invalid_api_key" } }""")]
    [InlineData("""{ "error": { "message": "Rate limit reached.", "type": "rate_limit_error", "param": "max_tokens", "code": "rate_limit_exceeded" } }""")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public void OpenAI_Only_learns_from_errors_that_name_a_rejected_parameter(string errorBody)
    {
        var current = new OpenAiCapabilities(UseMaxCompletionTokens: false, SupportsSampling: true);

        Assert.Null(OpenAiCapabilities.Downgrade(current, errorBody));
    }

    private static async Task<(LlmCompletionResponse Response, StubHandler Handler)> RunOpenAiAsync(string model, int maxTokens)
    {
        var handler = new StubHandler("""{ "choices": [{ "finish_reason": "stop", "message": { "role": "assistant", "content": "Done." } }] }""");
        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", model));
        var response = await provider.CompleteAsync(Conversation() with { MaxTokens = maxTokens });
        return (response, handler);
    }

    /// <summary>Answers the first request with an error and every later one with a real response,
    /// recording every body so a test can assert what the provider actually re-sent.</summary>
    private sealed class RejectOnceHandler(string errorBody, string successBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<JsonObject> Bodies { get; } = [];
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Bodies.Add(body);
            var first = RequestCount++ == 0;
            return new HttpResponseMessage(first ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(first ? errorBody : successBody)
            };
        }
    }

    // ---- OpenAI Responses API ---------------------------------------------------

    [Theory]
    // gpt-6 refuses function tools on Chat Completions at every reasoning effort, so these must
    // go to /v1/responses. Earlier reasoning models still take tools on Chat Completions.
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("openai/gpt-6-astra", true)]
    [InlineData("gpt-5.6-terra", false)]
    [InlineData("gpt-5", false)]
    [InlineData("o3", false)]
    [InlineData("gpt-4o", false)]
    public void OpenAI_Knows_which_endpoint_serves_tools_for_a_model(string model, bool expectsResponses)
    {
        Assert.Equal(expectsResponses, OpenAiCapabilities.Detect(model).RequiresResponsesApi);
    }

    [Fact]
    public async Task OpenAI_Reasoning_model_that_needs_Responses_sends_tools_there_in_the_Responses_shape()
    {
        var handler = new RecordingHandler("""
            { "status": "completed", "output": [{ "type": "function_call", "call_id": "call_1",
                "name": "spawn_agent", "arguments": "{\"role\":\"db\"}" }],
              "usage": { "input_tokens": 30, "output_tokens": 5,
                         "input_tokens_details": { "cached_tokens": 12 } } }
            """);

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "gpt-6-astra"));
        var response = await provider.CompleteAsync(Conversation());

        Assert.EndsWith("v1/responses", handler.Urls[0].ToString());

        var body = handler.Bodies[0];
        // Tools are a flat array here, with no "function" wrapper.
        var tool = body["tools"]![0]!.AsObject();
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("spawn_agent", tool["name"]!.GetValue<string>());
        Assert.Null(tool["function"]);

        // The system prompt becomes "instructions" and is not duplicated into the input.
        Assert.Equal("You are the root agent.", body["instructions"]!.GetValue<string>());
        Assert.All(body["input"]!.AsArray(), item => Assert.NotEqual("system", item!["role"]?.GetValue<string>()));

        // The cap is renamed here too, and conversations are not retained server-side.
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.True(body["max_output_tokens"]!.GetValue<int>() >= 25_000);
        Assert.False(body["temperature"] is not null);
        Assert.False(body["store"]!.GetValue<bool>());

        Assert.Equal(LlmFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("spawn_agent", call.Name);
        Assert.Equal("""{"role":"db"}""", call.ArgumentsJson);
        Assert.Equal(30, response.InputTokens);
        Assert.Equal(12, response.CachedInputTokens);
    }

    [Fact]
    public async Task OpenAI_Responses_body_carries_a_tool_round_trip_as_typed_items()
    {
        var handler = new RecordingHandler("""
            { "status": "completed", "output": [{ "type": "message",
                "content": [{ "type": "output_text", "text": "Done." }] }],
              "usage": { "input_tokens": 40, "output_tokens": 3 } }
            """);

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "gpt-6-astra"));
        var response = await provider.CompleteAsync(Conversation());

        var input = handler.Bodies[0]["input"]!.AsArray();
        // user, the assistant's text, its call, that call's result, then the next user turn.
        // The prior turn carried both prose and a call, so it becomes two items — a Responses
        // conversation is a typed list, not a list of messages with optional tool_calls.
        Assert.Equal(5, input.Count);
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", input[1]!["role"]!.GetValue<string>());
        Assert.Equal("Checking who exists.", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("function_call", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("prev_1", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("function_call_output", input[3]!["type"]!.GetValue<string>());
        Assert.Equal("prev_1", input[3]!["call_id"]!.GetValue<string>());
        Assert.Equal("""{"agents":[]}""", input[3]!["output"]!.GetValue<string>());

        Assert.Equal("Done.", response.Content);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task OpenAI_Responses_incomplete_output_reports_hitting_the_cap()
    {
        // Otherwise a truncated answer is indistinguishable from a finished one, and the agent
        // treats a half-written summary as a final one.
        var handler = new RecordingHandler("""
            { "status": "incomplete", "incomplete_details": { "reason": "max_output_tokens" },
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": "Part one" }] }] }
            """);

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "gpt-6-astra"));
        var response = await provider.CompleteAsync(Conversation());

        Assert.Equal(LlmFinishReason.MaxTokens, response.FinishReason);
        Assert.Equal("Part one", response.Content);
    }

    [Fact]
    public async Task OpenAI_Moves_to_Responses_when_a_model_refuses_tools_on_Chat_Completions()
    {
        // A model id nothing predicted: the provider has to discover this from the API.
        var handler = new RejectOnceHandler(
            """
            { "error": { "message": "Function tools with reasoning_effort are not supported for some-new-model in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.",
              "type": "invalid_request_error", "param": "reasoning_effort", "code": null } }
            """,
            """
            { "status": "completed", "output": [{ "type": "function_call", "call_id": "call_9",
                "name": "spawn_agent", "arguments": "{}" }] }
            """);

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "some-new-model"));
        var response = await provider.CompleteAsync(Conversation());

        Assert.Equal("spawn_agent", Assert.Single(response.ToolCalls).Name);
        Assert.Equal(2, handler.RequestCount);
        Assert.EndsWith("v1/chat/completions", handler.Urls[0]);
        Assert.EndsWith("v1/responses", handler.Urls[1]);
        Assert.Equal(25_000, handler.Bodies[1]["max_output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task OpenAI_Stays_on_Chat_Completions_for_models_that_accept_tools_there()
    {
        var handler = new RecordingHandler("""
            { "choices": [{ "finish_reason": "tool_calls", "message": { "role": "assistant", "content": null,
              "tool_calls": [{ "id": "call_1", "type": "function", "function": { "name": "spawn_agent", "arguments": "{}" } }] } }] }
            """);

        var provider = new OpenAIProvider(new HttpClient(handler) { BaseAddress = new Uri("https://llm.test/") },
            Opts("OpenAI", "gpt-5"));
        var response = await provider.CompleteAsync(Conversation());

        Assert.EndsWith("v1/chat/completions", handler.Urls[0].ToString());
        Assert.Equal("spawn_agent", Assert.Single(response.ToolCalls).Name);
        Assert.Equal(LlmFinishReason.ToolCalls, response.FinishReason);
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
