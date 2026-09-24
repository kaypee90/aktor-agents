using System.Net;
using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

public class OllamaProviderTests
{
    /// <summary>Replays the given responses in order (the last one repeats) and records requests.</summary>
    private sealed class StubHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody => RequestBodies.LastOrDefault();
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            var (status, body) = responses[Math.Min(RequestBodies.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private const string OkBody = """{ "message": { "role": "assistant", "content": "hi" }, "done_reason": "stop" }""";

    private static (OllamaProvider Provider, StubHandler Handler) Create(string responseBody, HttpStatusCode status = HttpStatusCode.OK) =>
        Create((status, responseBody));

    private static (OllamaProvider Provider, StubHandler Handler) Create(params (HttpStatusCode, string)[] responses)
    {
        var handler = new StubHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        var options = Options.Create(new LlmOptions { Provider = "Ollama", Model = "qwen2.5:7b", ContextLength = 12000 });
        return (new OllamaProvider(client, options), handler);
    }

    private static readonly LlmToolDefinition SayTool = new()
    {
        Name = "say",
        Description = "Speak.",
        JsonSchema = """{ "type": "object", "properties": { "text": { "type": "string" } } }"""
    };

    [Fact]
    public async Task Request_UsesNativeChat_WithContextWindow_AndObjectToolArguments()
    {
        var (provider, handler) = Create("""{ "message": { "role": "assistant", "content": "hi" }, "done_reason": "stop" }""");

        await provider.CompleteAsync(new LlmCompletionRequest
        {
            Messages =
            [
                ChatMessage.System("sys"),
                new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    ToolCalls = [new ToolCall { Id = "call_1", Name = "say", ArgumentsJson = """{"text":"hello"}""" }]
                },
                new ChatMessage { Role = ChatRole.Tool, ToolCallId = "call_1", ToolName = "say", Content = """{"ok":true}""" }
            ],
            Tools = [SayTool]
        });

        Assert.Equal("http://ollama.test:11434/api/chat", handler.Request!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
        Assert.Equal(12000, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("say", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

        var assistant = root.GetProperty("messages")[1];
        // Ollama wants arguments as an object, not a JSON string.
        var args = assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Equal("hello", args.GetProperty("text").GetString());

        var tool = root.GetProperty("messages")[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("say", tool.GetProperty("tool_name").GetString());
    }

    [Fact]
    public void Response_ToolCallsWithoutIds_GetGeneratedIds_AndTokenCounts()
    {
        var response = OllamaProvider.ParseResponse("""
        {
          "message": {
            "role": "assistant",
            "content": "",
            "tool_calls": [
              { "function": { "name": "say", "arguments": { "text": "hi" } } },
              { "function": { "name": "end_turn", "arguments": { "plan": "rest" } } }
            ]
          },
          "done_reason": "stop",
          "prompt_eval_count": 812,
          "eval_count": 40
        }
        """);

        Assert.Equal(LlmFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(2, response.ToolCalls.Count);
        Assert.All(response.ToolCalls, c => Assert.StartsWith("call_", c.Id));
        Assert.NotEqual(response.ToolCalls[0].Id, response.ToolCalls[1].Id);
        Assert.Equal("hi", JsonDocument.Parse(response.ToolCalls[0].ArgumentsJson).RootElement.GetProperty("text").GetString());
        Assert.Equal(812, response.InputTokens);
        Assert.Equal(40, response.OutputTokens);
        Assert.Null(response.Content);
    }

    [Fact]
    public void Response_StripsThinkBlocks()
    {
        var response = OllamaProvider.ParseResponse("""
        { "message": { "role": "assistant", "content": "<think>secret reasoning</think>\nThe answer.", "thinking": "more" }, "done_reason": "stop" }
        """);

        Assert.Equal("The answer.", response.Content);
    }

    [Fact]
    public void Response_StripsUnclosedThinkBlock()
    {
        var response = OllamaProvider.ParseResponse("""
        { "message": { "role": "assistant", "content": "<think>cut off mid-reasoning" }, "done_reason": "length" }
        """);

        Assert.Null(response.Content);
    }

    [Fact]
    public async Task Request_AsksModelNotToThink()
    {
        var (provider, handler) = Create(OkBody);

        await provider.CompleteAsync(new LlmCompletionRequest { Messages = [ChatMessage.User("hi")] });

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task OlderOllama_RejectingThinkField_IsRetriedWithoutIt_AndNotSentAgain()
    {
        var (provider, handler) = Create(
            (HttpStatusCode.BadRequest, """{"error":"json: unknown field \"think\""}"""),
            (HttpStatusCode.OK, OkBody));

        var first = await provider.CompleteAsync(new LlmCompletionRequest { Messages = [ChatMessage.User("hi")] });
        await provider.CompleteAsync(new LlmCompletionRequest { Messages = [ChatMessage.User("again")] });

        Assert.Equal("hi", first.Content);
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("\"think\"", handler.RequestBodies[0]);
        Assert.DoesNotContain("\"think\"", handler.RequestBodies[1]);
        Assert.DoesNotContain("\"think\"", handler.RequestBodies[2]);
    }

    [Fact]
    public async Task ModelWithoutToolSupport_GivesActionableError()
    {
        var (provider, _) = Create("""{"error":"registry.ollama.ai/library/gemma:2b does not support tools"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new LlmCompletionRequest
        {
            Messages = [ChatMessage.User("hi")],
            Tools = [SayTool]
        }));

        Assert.Contains("tool calling", ex.Message);
    }
}
