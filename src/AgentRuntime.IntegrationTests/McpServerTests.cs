using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Tasks;
using AgentRuntime.IntegrationTests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P1: Aktor as an MCP server. An MCP client starts a task, waits for it (long poll and
/// progress notifications), reads the result and the team; another organization's key sees none
/// of it; completion webhooks arrive signed; and the task ceiling applies to MCP callers too.
/// </summary>
public sealed class McpServerTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private readonly ApiTestHost _host = fixture.Host;

    private bool Skip()
    {
        if (_host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + _host.Unavailable);
        return true;
    }

    private async Task<McpClient> ConnectAsync(Organization org)
    {
        var http = _host.ClientFor(org);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Json(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Tool_round_trip_start_wait_result_and_team()
    {
        if (Skip()) return;
        var org = await _host.CreateOrganizationAsync("McpRoundTrip");
        await using var mcp = await ConnectAsync(org);

        var tools = (await mcp.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "run_goal", "get_task_status", "get_task_result", "cancel_task", "list_agents" }, tools);

        var started = Json(await mcp.CallToolAsync("run_goal", new Dictionary<string, object?>
        {
            ["goal"] = "Research the feasibility of an AI-powered property management SaaS.",
            ["correlation_id"] = "n8n-run-42"
        }));
        var taskId = started.GetProperty("task_id").GetString()!;
        Assert.Equal("n8n-run-42", started.GetProperty("correlation_id").GetString());
        Assert.Equal($"http://dashboard.test/?task={taskId}", started.GetProperty("dashboard_url").GetString());

        // Long poll, with progress notifications on the way.
        var progress = new ConcurrentQueue<ProgressNotificationValue>();
        var status = Json(await mcp.CallToolAsync("get_task_status",
            new Dictionary<string, object?> { ["task_id"] = taskId, ["wait_seconds"] = 90 },
            new Progress(progress)));
        Assert.True(status.GetProperty("done").GetBoolean(), status.ToString());
        Assert.Equal("completed", status.GetProperty("state").GetString());
        Assert.Equal("n8n-run-42", status.GetProperty("correlation_id").GetString());

        var result = Json(await mcp.CallToolAsync("get_task_result", new Dictionary<string, object?> { ["task_id"] = taskId }));
        Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("result").GetProperty("summary").GetString()));
        Assert.True(result.GetProperty("result").GetProperty("participating_agents").GetInt32() >= 2);

        var team = Json(await mcp.CallToolAsync("list_agents", new Dictionary<string, object?> { ["task_id"] = taskId }));
        var roles = team.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("role").GetString()).ToList();
        Assert.Contains("Root Agent", roles);
        Assert.True(roles.Count >= 3, string.Join(", ", roles));

        // The correlation id is stamped on the run's persisted events too.
        using var api = _host.ClientFor(org);
        var events = await api.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/events?limit=500");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("correlation_id").GetString() == "n8n-run-42" && e.GetProperty("type").GetString() == "AgentSpawned");
    }

    [Fact]
    public async Task Run_goal_with_wait_blocks_until_done_and_reports_progress()
    {
        if (Skip()) return;
        var org = await _host.CreateOrganizationAsync("McpWait");
        await using var mcp = await ConnectAsync(org);

        var progress = new ConcurrentQueue<ProgressNotificationValue>();
        var done = Json(await mcp.CallToolAsync("run_goal",
            new Dictionary<string, object?> { ["goal"] = "Add user authentication to this application.", ["wait"] = true },
            new Progress(progress)));

        Assert.True(done.GetProperty("done").GetBoolean(), done.ToString());
        Assert.StartsWith("corr-", done.GetProperty("correlation_id").GetString());
        Assert.NotEmpty(progress);
        output.WriteLine(string.Join("\n", progress.Select(p => $"{p.Progress}/{p.Total} {p.Message}")));
    }

    [Fact]
    public async Task Another_organization_cannot_see_or_cancel_the_task()
    {
        if (Skip()) return;
        var owner = await _host.CreateOrganizationAsync("McpOwner");
        var other = await _host.CreateOrganizationAsync("McpOther");
        await using var ownerMcp = await ConnectAsync(owner);
        await using var otherMcp = await ConnectAsync(other);

        var taskId = Json(await ownerMcp.CallToolAsync("run_goal", new Dictionary<string, object?> { ["goal"] = "Summarize our onboarding docs." }))
            .GetProperty("task_id").GetString()!;

        foreach (var tool in new[] { "get_task_status", "get_task_result", "cancel_task", "list_agents" })
        {
            var refused = await otherMcp.CallToolAsync(tool, new Dictionary<string, object?> { ["task_id"] = taskId });
            Assert.True(refused.IsError, $"{tool} should be refused across organizations");
            Assert.Contains("No task", Text(refused));
        }

        // REST agrees: someone else's task is indistinguishable from one that doesn't exist.
        using var otherApi = _host.ClientFor(other);
        Assert.Equal(HttpStatusCode.NotFound, (await otherApi.GetAsync($"/api/tasks/{taskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherApi.PostAsync($"/api/tasks/{taskId}/cancel", null)).StatusCode);

        // And the owner still sees it.
        var status = Json(await ownerMcp.CallToolAsync("get_task_status", new Dictionary<string, object?> { ["task_id"] = taskId, ["wait_seconds"] = 60 }));
        Assert.Equal(taskId, status.GetProperty("task_id").GetString());
    }

    [Fact]
    public async Task Mcp_requires_an_api_key()
    {
        if (Skip()) return;
        using var anonymous = _host.Factory.CreateClient();
        var response = await anonymous.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_keys_can_read_but_not_start_tasks()
    {
        if (Skip()) return;
        var viewer = await _host.CreateOrganizationAsync("McpViewer", Tenancy.TenantRole.Viewer);
        await using var mcp = await ConnectAsync(viewer);
        var refused = await RunGoalExpectingRefusalAsync(mcp);
        Assert.NotNull(refused);
    }

    private static async Task<string?> RunGoalExpectingRefusalAsync(McpClient mcp)
    {
        try
        {
            var result = await mcp.CallToolAsync("run_goal", new Dictionary<string, object?> { ["goal"] = "Do something." });
            return result.IsError == true ? Text(result) : null;
        }
        catch (McpException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public async Task Budget_above_the_task_ceiling_is_cut_to_it()
    {
        if (Skip()) return;
        var org = await _host.CreateOrganizationAsync("McpCeiling");
        await using var mcp = await ConnectAsync(org);

        var started = Json(await mcp.CallToolAsync("run_goal", new Dictionary<string, object?>
        {
            ["goal"] = "Write a short note.",
            ["budget"] = new Dictionary<string, object?> { ["max_cost_usd"] = 100_000, ["max_tokens"] = 1_000_000_000 }
        }));

        var budget = started.GetProperty("budget");
        Assert.Equal(50m, budget.GetProperty("max_cost_usd").GetDecimal());
        Assert.Equal(5_000_000, budget.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Completion_webhook_is_delivered_signed()
    {
        if (Skip()) return;
        var org = await _host.CreateOrganizationAsync("McpWebhook");
        await using var mcp = await ConnectAsync(org);

        var port = Random.Shared.Next(40000, 50000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var signature = ctx.Request.Headers["X-Aktor-Signature"];
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return (body, signature);
        });

        var taskId = Json(await mcp.CallToolAsync("run_goal", new Dictionary<string, object?>
        {
            ["goal"] = "Research competitors for a property management SaaS.",
            ["callback_url"] = $"http://127.0.0.1:{port}/aktor-done",
            ["callback_secret"] = "s3cret"
        })).GetProperty("task_id").GetString()!;

        var winner = await Task.WhenAny(received, Task.Delay(TimeSpan.FromSeconds(90)));
        Assert.Same(received, winner);
        var (body, signature) = await received;

        Assert.Equal("sha256=" + TaskCallbackDispatcher.Sign("s3cret", body), signature);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("task.completed", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(taskId, doc.RootElement.GetProperty("task_id").GetString());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("participating_agents").GetInt32() >= 1);
    }

    private sealed class Progress(ConcurrentQueue<ProgressNotificationValue> sink) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => sink.Enqueue(value);
    }
}

/// <summary>One API host (and Postgres container) shared by a test class.</summary>
public sealed class ApiTestHostFixture : IAsyncLifetime
{
    public ApiTestHost Host { get; } = new();
    public Task InitializeAsync() => Host.InitializeAsync();
    public Task DisposeAsync() => Host.DisposeAsync();
}
