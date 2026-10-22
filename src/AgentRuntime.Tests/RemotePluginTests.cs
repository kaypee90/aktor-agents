using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests;

/// <summary>The remote-plugin protocol from the runtime's side (docs/plugins.md#remote-plugins).</summary>
public class RemotePluginTests
{
    /// <summary>A plugin service: answers by path, and records what it was sent.</summary>
    private sealed class FakeService(Func<string, JsonObject, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(string Path, JsonObject Body, string? Auth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Calls.Add((request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            var (status, text) = answer(request.RequestUri.AbsolutePath, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PluginConnection Connection(string url = "https://plugins.example.com/crm/") => new()
    {
        ConnectionId = "conn-1",
        WorkspaceId = "ws-1",
        Name = "crm",
        Settings = new Dictionary<string, string> { ["base_url"] = url, ["config"] = """{"region":"eu"}""" },
        Secrets = new Dictionary<string, string> { ["token"] = "shared", ["credentials"] = """{"api_key":"k-123"}""" }
    };

    [Fact]
    public async Task Tools_are_listed_with_their_side_effects_and_called_with_the_idempotency_key()
    {
        var service = new FakeService((path, body) => path switch
        {
            "/crm/tools/list" => (HttpStatusCode.OK, """
                {"tools":[{"name":"find","description":"Find one.","input_schema":{"type":"object","properties":{"q":{"type":"string"}}},"side_effects":"read_only"},
                          {"name":"write","side_effects":"idempotent"},{"name":"mystery"}]}
                """),
            "/crm/tools/call" => (HttpStatusCode.OK, body["tool"]!.GetValue<string>() == "find" ? """{"ok":true,"result":{"found":1}}""" : """{"ok":false,"error":"nope","code":"bad"}"""),
            _ => (HttpStatusCode.NotFound, """{"error":"no"}""")
        });
        var plugin = new RemotePlugin(new Factory(service));

        var tools = await plugin.ListToolsAsync(Connection(), CancellationToken.None);
        Assert.Equal([ToolSideEffects.ReadOnly, ToolSideEffects.Idempotent, ToolSideEffects.NonIdempotent], tools.Select(t => t.SideEffects));
        Assert.Contains("\"q\"", tools[0].JsonSchema);

        var ok = await plugin.ExecuteToolAsync(Connection(), "find", new ToolExecutionRequest
        {
            ToolName = "crm__find", AgentId = "agent-1", TaskId = "task-1", ArgumentsJson = """{"q":"ada"}""", IdempotencyKey = "agent-1:call-9"
        });
        Assert.True(ok.Success);
        Assert.Equal(1, JsonNode.Parse(ok.ResultJson)!["found"]!.GetValue<int>());

        var call = service.Calls.Single(c => c.Path == "/crm/tools/call");
        Assert.Equal("Bearer shared", call.Auth);
        Assert.Equal("agent-1:call-9", call.Body["idempotency_key"]!.GetValue<string>());
        Assert.Equal("ada", call.Body["arguments"]!["q"]!.GetValue<string>());
        Assert.Equal("eu", call.Body["connection"]!["config"]!["region"]!.GetValue<string>());
        Assert.Equal("k-123", call.Body["credentials"]!["api_key"]!.GetValue<string>());

        var failed = await plugin.ExecuteToolAsync(Connection(), "write", new ToolExecutionRequest { ToolName = "crm__write", AgentId = "a", TaskId = "t", ArgumentsJson = "{}" });
        Assert.False(failed.Success);
        Assert.Equal(("nope", "bad"), (failed.ErrorMessage, failed.ErrorCode));
    }

    [Fact]
    public async Task Validation_notifications_and_inbound_follow_the_service()
    {
        var service = new FakeService((path, _) => path switch
        {
            "/validate" => (HttpStatusCode.OK, """{"ok":false,"message":"Bad API key."}"""),
            "/notify" => (HttpStatusCode.OK, """{"delivered":false,"error":"rate limited","retryable":true}"""),
            "/inbound" => (HttpStatusCode.OK, """{"is_message":true,"sender_id":"+1555","text":"approve A1","message_id":"m1","response_body":"ok"}"""),
            _ => (HttpStatusCode.InternalServerError, """{"error":"boom"}""")
        });
        var plugin = new RemotePlugin(new Factory(service));
        var connection = Connection("https://plugins.example.com");

        var check = await plugin.ValidateAsync(connection, CancellationToken.None);
        Assert.False(check.Ok);
        Assert.Equal("Bad API key.", check.Message);

        var delivery = await plugin.SendNotificationAsync(connection, new OutboundNotification { DeliveryId = "d1", WorkspaceName = "W", AuthorName = "A", Text = "hi" }, CancellationToken.None);
        Assert.False(delivery.Delivered);
        Assert.True(delivery.Retryable);

        var inbound = await plugin.ParseInboundAsync(connection, new InboundRequest { Body = "x" }, CancellationToken.None);
        Assert.True(inbound.IsMessage);
        Assert.Equal(("+1555", "approve A1", "m1", "ok"), (inbound.SenderId, inbound.Text, inbound.MessageId, inbound.ResponseBody));

        // A service that's down is a failed check, not an exception.
        var unreachable = new RemotePlugin(new Factory(new FakeService((_, _) => (HttpStatusCode.BadGateway, "{}"))));
        Assert.False((await unreachable.ValidateAsync(connection, CancellationToken.None)).Ok);
        Assert.False((await new RemotePlugin(new Factory(service)).ValidateAsync(Connection("ftp://x"), CancellationToken.None)).Ok);
    }
}
