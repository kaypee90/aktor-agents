using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P5 through the real API: Aktor as an A2A agent (protocol 1.0 and 0.3, blocking, polling
/// and streaming) and as an ACP agent over its WebSocket endpoint, both on the same task service,
/// with tenant isolation and API-key auth.
/// </summary>
public sealed class A2aAcpTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;
    private const string Goal = "Research the feasibility of building an AI-powered property management SaaS.";

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonObject> Rpc(HttpClient http, string method, object @params, string? version = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json")
        };
        if (version is not null) request.Headers.Add("A2A-Version", version);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return (JsonObject)JsonNode.Parse(body)!;
    }

    private static object TextMessage(string text, bool v1) => v1
        ? new { messageId = Guid.NewGuid().ToString("n"), role = "ROLE_USER", parts = new object[] { new { text } } }
        : new { kind = "message", messageId = Guid.NewGuid().ToString("n"), role = "user", parts = new object[] { new { kind = "text", text } } };

    [Fact]
    public async Task Agent_card_is_public_and_versioned()
    {
        if (Skip()) return;
        using var anonymous = Host.Factory.CreateClient();

        var legacy = JsonNode.Parse(await anonymous.GetStringAsync("/.well-known/agent-card.json"))!;
        Assert.Equal("0.3.0", legacy["protocolVersion"]!.GetValue<string>());
        Assert.EndsWith("/a2a", legacy["url"]!.GetValue<string>());
        Assert.Equal("JSONRPC", legacy["preferredTransport"]!.GetValue<string>());
        Assert.Equal("bearer", legacy["securitySchemes"]!["aktorApiKey"]!["scheme"]!.GetValue<string>());
        Assert.Equal("run-goal", legacy["skills"]![0]!["id"]!.GetValue<string>());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        request.Headers.Add("A2A-Version", "1.0");
        var v1 = JsonNode.Parse(await (await anonymous.SendAsync(request)).Content.ReadAsStringAsync())!;
        Assert.Equal("1.0", v1["supportedInterfaces"]![0]!["protocolVersion"]!.GetValue<string>());
        Assert.True(v1["capabilities"]!["streaming"]!.GetValue<bool>());
        Assert.NotNull(v1["securitySchemes"]!["aktorApiKey"]!["httpAuthSecurityScheme"]);
    }

    [Fact]
    public async Task V03_blocking_message_send_returns_the_completed_task_with_its_result()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("A2aLegacy");
        using var http = Host.ClientFor(org);

        var response = await Rpc(http, "message/send", new { message = TextMessage(Goal, v1: false), configuration = new { blocking = true } });
        output.WriteLine(response.ToJsonString());
        var task = response["result"]!;
        Assert.Equal("task", task["kind"]!.GetValue<string>());
        Assert.Equal("completed", task["status"]!["state"]!.GetValue<string>());
        Assert.Equal("agent", task["status"]!["message"]!["role"]!.GetValue<string>());

        var parts = task["artifacts"]![0]!["parts"]!.AsArray();
        Assert.Contains(parts, p => p!["kind"]!.GetValue<string>() == "text" && p["text"]!.GetValue<string>().Contains("Full run:"));
        var data = parts.Single(p => p!["kind"]!.GetValue<string>() == "data")!["data"]!;
        Assert.True(data["participating_agents"]!.GetValue<int>() >= 1);
        Assert.StartsWith("http://dashboard.test/?task=", task["metadata"]!["aktor"]!["dashboard_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task V1_send_then_poll_GetTask_until_completed()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("A2aV1");
        using var http = Host.ClientFor(org);

        var sent = await Rpc(http, "SendMessage", new { message = TextMessage(Goal, v1: true), configuration = new { returnImmediately = true } }, "1.0");
        var task = sent["result"]!["task"]!;
        var id = task["id"]!.GetValue<string>();
        Assert.Null(task["kind"]);
        Assert.StartsWith("TASK_STATE_", task["status"]!["state"]!.GetValue<string>());

        JsonNode? current = null;
        for (var i = 0; i < 180; i++)
        {
            current = (await Rpc(http, "GetTask", new { id, historyLength = 1 }, "1.0"))["result"];
            if (current!["status"]!["state"]!.GetValue<string>() != "TASK_STATE_WORKING") break;
            await Task.Delay(500);
        }

        Assert.Equal("TASK_STATE_COMPLETED", current!["status"]!["state"]!.GetValue<string>());
        Assert.Equal("ROLE_USER", current["history"]![0]!["role"]!.GetValue<string>());
        var parts = current["artifacts"]![0]!["parts"]!.AsArray();
        Assert.Contains(parts, p => p!["text"] is not null);
        Assert.Contains(parts, p => p!["data"] is not null && p["mediaType"]!.GetValue<string>() == "application/json");

        // A finished task can't be cancelled.
        var refused = await Rpc(http, "CancelTask", new { id }, "1.0");
        Assert.Equal(-32002, refused["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Streaming_sends_the_task_progress_the_result_and_a_final_status()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("A2aStream");
        using var http = Host.ClientFor(org);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "message/stream", @params = new { message = TextMessage(Goal, v1: false) } }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var events = new List<JsonNode>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var result = JsonNode.Parse(line[6..])!["result"]!;
            events.Add(result);
            if (result["kind"]?.GetValue<string>() == "status-update" && result["final"]!.GetValue<bool>()) break;
        }

        Assert.Equal("task", events[0]["kind"]!.GetValue<string>());
        Assert.Contains(events, e => e["kind"]!.GetValue<string>() == "artifact-update");
        var last = events[^1];
        Assert.Equal("completed", last["status"]!["state"]!.GetValue<string>());
        Assert.True(events.Count(e => e["kind"]!.GetValue<string>() == "status-update") >= 2, "progress updates before the final one");
    }

    [Fact]
    public async Task Another_organization_cannot_read_or_cancel_and_auth_is_required()
    {
        if (Skip()) return;
        var owner = await Host.CreateOrganizationAsync("A2aOwner");
        var other = await Host.CreateOrganizationAsync("A2aOther");
        using var ownerHttp = Host.ClientFor(owner);
        using var otherHttp = Host.ClientFor(other);

        var id = (await Rpc(ownerHttp, "message/send", new { message = TextMessage("Summarize our docs.", false), configuration = new { blocking = false } }))["result"]!["id"]!.GetValue<string>();
        Assert.Equal(-32001, (await Rpc(otherHttp, "tasks/get", new { id }))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32001, (await Rpc(otherHttp, "tasks/cancel", new { id }))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await Rpc(ownerHttp, "tasks/unknown", new { id }))["error"]!["code"]!.GetValue<int>());

        using var anonymous = Host.Factory.CreateClient();
        var unauth = await anonymous.PostAsync("/a2a", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tasks/get","params":{"id":"x"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
    }

    // ---- ACP ---------------------------------------------------------------------

    private sealed class AcpClient(WebSocket socket) : IAsyncDisposable
    {
        private int _id;
        public List<JsonNode> Updates { get; } = [];

        public async Task NotifyAsync(string method, object @params) =>
            await SendAsync(new { jsonrpc = "2.0", method, @params });

        public async Task<JsonNode> CallAsync(string method, object @params, TimeSpan? timeout = null)
        {
            var id = ++_id;
            await SendAsync(new { jsonrpc = "2.0", id, method, @params });
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(120));
            while (true)
            {
                var message = await ReceiveAsync(cts.Token);
                if (message["method"]?.GetValue<string>() == "session/update") Updates.Add(message["params"]!["update"]!);
                else if (message["id"]?.GetValue<int>() == id) return message;
            }
        }

        private Task SendAsync(object message) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

        private async Task<JsonNode> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[1 << 16];
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            return JsonNode.Parse(ms.ToArray())!;
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            socket.Dispose();
        }
    }

    private async Task<AcpClient> ConnectAcpAsync(Organization org)
    {
        var client = Host.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {org.ApiKey}";
        var socket = await client.ConnectAsync(new Uri(Host.Factory.Server.BaseAddress, "/acp"), CancellationToken.None);
        return new AcpClient(socket);
    }

    [Fact]
    public async Task Acp_session_runs_a_prompt_as_a_task_and_streams_the_team()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Acp");
        await using var acp = await ConnectAcpAsync(org);

        var init = await acp.CallAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { } });
        Assert.Equal(1, init["result"]!["protocolVersion"]!.GetValue<int>());
        Assert.Equal("aktor", init["result"]!["agentInfo"]!["name"]!.GetValue<string>());

        var session = (await acp.CallAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() }))["result"]!["sessionId"]!.GetValue<string>();
        var prompt = await acp.CallAsync("session/prompt", new { sessionId = session, prompt = new object[] { new { type = "text", text = Goal } } });

        Assert.Equal("end_turn", prompt["result"]!["stopReason"]!.GetValue<string>());
        var kinds = acp.Updates.Select(u => u["sessionUpdate"]!.GetValue<string>()).ToList();
        output.WriteLine(string.Join(", ", kinds));
        Assert.Equal("agent_message_chunk", kinds[0]);
        Assert.Contains("tool_call", kinds);
        Assert.Contains("plan", kinds);
        var final = acp.Updates.Last(u => u["sessionUpdate"]!.GetValue<string>() == "agent_message_chunk")["content"]!["text"]!.GetValue<string>();
        Assert.Contains("Full run: http://dashboard.test/?task=", final);

        // A follow-up prompt in the same session is a new task that builds on the first.
        var followUp = await acp.CallAsync("session/prompt", new { sessionId = session, prompt = new object[] { new { type = "text", text = "Now summarize the risks." } } });
        Assert.Equal("end_turn", followUp["result"]!["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Acp_cancel_stops_the_prompt()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("AcpCancel");
        await using var acp = await ConnectAcpAsync(org);
        await acp.CallAsync("initialize", new { protocolVersion = 1 });
        var session = (await acp.CallAsync("session/new", new { cwd = "/tmp", mcpServers = Array.Empty<object>() }))["result"]!["sessionId"]!.GetValue<string>();

        var promptTask = acp.CallAsync("session/prompt", new { sessionId = session, prompt = new object[] { new { type = "text", text = Goal } } });
        await acp.NotifyAsync("session/cancel", new { sessionId = session });
        var prompt = await promptTask;
        Assert.Equal("cancelled", prompt["result"]!["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Acp_requires_an_api_key()
    {
        if (Skip()) return;
        var client = Host.Factory.Server.CreateWebSocketClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri(Host.Factory.Server.BaseAddress, "/acp"), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }
}
