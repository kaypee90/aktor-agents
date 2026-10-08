using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// A REST API added as an HTTP API connection, with endpoints typed by hand, served as its own MCP
/// server (docs/plugins.md, "MCP gateway"): a real MCP client lists and calls the endpoints; the
/// stored credential reaches the API but never the caller; the workspace's safety policy and audit
/// log apply; and nothing is served until the owner switches the gateway on, or to another
/// organization.
/// </summary>
public sealed class ConnectionGatewayTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<McpClient> ConnectAsync(Organization org, string path)
    {
        var http = Host.ClientFor(org);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, path),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>A tiny REST API on a free local port: GET /v1, GET /v1/orders/{id}, POST /v1/orders/{id}/refunds.</summary>
    private sealed class FakeStoreApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        public ConcurrentQueue<(string Method, string Path, string? Auth, string Body)> Requests { get; } = new();
        public string BaseUrl { get; }

        public FakeStoreApi()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var path = context.Request.Url!.PathAndQuery;
                Requests.Enqueue((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));
                var reply = context.Request.HttpMethod == "POST"
                    ? """{"refund":"r-1","status":"created"}"""
                    : path.StartsWith("/v1/orders/") ? $$"""{"id":"{{path["/v1/orders/".Length..]}}","total":42.5}""" : """{"api":"store"}""";
                var bytes = Encoding.UTF8.GetBytes(reply);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private const string Endpoints = """
    [
      { "name": "get_order", "method": "GET", "path": "/orders/{order_id}", "description": "One order with its total." },
      { "name": "create_refund", "method": "POST", "path": "/orders/{order_id}/refunds",
        "body_schema": { "type": "object", "properties": { "amount": { "type": "number" } } } }
    ]
    """;

    [Fact]
    public async Task A_rest_api_with_hand_written_endpoints_is_served_as_an_mcp_server_under_the_workspace_rules()
    {
        if (Skip()) return;
        using var store = new FakeStoreApi();
        var org = await Host.CreateOrganizationAsync("Gateway", Tenancy.TenantRole.Admin);
        var stranger = await Host.CreateOrganizationAsync("GatewayStranger", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);

        var workspaceId = (await Json(await api.PostAsJsonAsync("/api/workspaces", new { name = "Store desk", goal = "Answer questions about orders." })))
            .GetProperty("workspace_id").GetString()!;
        var added = await Json(await api.PostAsJsonAsync($"/api/workspaces/{workspaceId}/connections", new
        {
            plugin_id = "http-api",
            name = "store",
            settings = new Dictionary<string, string>
            {
                ["base_url"] = store.BaseUrl,
                ["description"] = "Our store's order API",
                ["allow_writes"] = "true",
                ["endpoints"] = Endpoints
            },
            secrets = new Dictionary<string, string> { ["auth_header_value"] = "Bearer sk_store_secret" }
        }));
        var connection = added.GetProperty("connection");
        var connectionId = connection.GetProperty("connection_id").GetString()!;
        var path = connection.GetProperty("gateway_path").GetString()!;
        Assert.Equal($"/mcp/gateway/{workspaceId}/{connectionId}", path);
        Assert.Equal(["store__get", "store__send", "store__get_order", "store__create_refund"],
            connection.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));

        // Nothing is served until the owner switches the gateway on.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(org, path));

        await Json(await api.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/connections/{connectionId}",
            new { gateway = new { enabled = true, tools = new[] { "get_order", "create_refund", "no_such_tool" } } }));

        await using (var mcp = await ConnectAsync(org, path))
        {
            var tools = await mcp.ListToolsAsync();
            Assert.Equal(["get_order", "create_refund"], tools.Select(t => t.Name));
            Assert.True(tools[0].ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.True(tools[1].ProtocolTool.Annotations?.DestructiveHint);

            var order = await mcp.CallToolAsync("get_order", new Dictionary<string, object?> { ["order_id"] = "17" });
            Assert.False(order.IsError == true, Text(order));
            Assert.Contains("42.5", Text(order));
            // The stored credential went to the API, not back to the caller.
            Assert.Contains(store.Requests, r => r.Path == "/v1/orders/17" && r.Auth == "Bearer sk_store_secret");
            Assert.DoesNotContain("sk_store_secret", Text(order));

            var refund = await mcp.CallToolAsync("create_refund", new Dictionary<string, object?> { ["order_id"] = "17", ["body"] = new { amount = 5 } });
            Assert.False(refund.IsError == true, Text(refund));
            Assert.Contains(store.Requests, r => r is { Method: "POST", Path: "/v1/orders/17/refunds" } && r.Body == """{"amount":5}""");

            // Under supervision, writes need a person's approval, which a gateway call can't wait for.
            await Json(await api.PutAsJsonAsync($"/api/workspaces/{workspaceId}/policy", new { autonomy = "Supervised" }));
            var posts = store.Requests.Count(r => r.Method == "POST");
            var refused = await mcp.CallToolAsync("create_refund", new Dictionary<string, object?> { ["order_id"] = "18", ["body"] = new { amount = 1 } });
            Assert.True(refused.IsError);
            Assert.Contains("approval", Text(refused));
            Assert.Equal(posts, store.Requests.Count(r => r.Method == "POST"));
            // Reads still run.
            Assert.False((await mcp.CallToolAsync("get_order", new Dictionary<string, object?> { ["order_id"] = "18" })).IsError == true);
        }

        // Every call is in the workspace's audit log, refused ones included.
        var audit = (await Json(await api.GetAsync($"/api/workspaces/{workspaceId}/audit?action=gateway.call"))).EnumerateArray().ToList();
        Assert.Equal(4, audit.Count);
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == "store__create_refund" && e.GetProperty("outcome").GetString() == "denied");
        Assert.All(audit, e => Assert.DoesNotContain("sk_store_secret", e.GetRawText()));

        // Another organization's key sees nothing; switching the gateway off closes it.
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(stranger, path));
        await Json(await api.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/connections/{connectionId}", new { gateway = new { enabled = false, tools = Array.Empty<string>() } }));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(org, path));
    }

    [Fact]
    public async Task Endpoints_can_be_edited_and_imported_from_openapi_after_connecting()
    {
        if (Skip()) return;
        using var store = new FakeStoreApi();
        var org = await Host.CreateOrganizationAsync("GatewayEdit", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);
        var workspaceId = (await Json(await api.PostAsJsonAsync("/api/workspaces", new { name = "Edits", goal = "Look up orders." })))
            .GetProperty("workspace_id").GetString()!;
        var connectionId = (await Json(await api.PostAsJsonAsync($"/api/workspaces/{workspaceId}/connections", new
        {
            plugin_id = "http-api",
            name = "store",
            settings = new Dictionary<string, string> { ["base_url"] = store.BaseUrl }
        }))).GetProperty("connection").GetProperty("connection_id").GetString()!;

        // An OpenAPI document becomes endpoints; nothing is saved until the connection is updated.
        var imported = await Json(await api.PostAsJsonAsync("/api/integrations/openapi", new
        {
            spec = """
            openapi: 3.1.0
            info: { title: Store, version: "1" }
            paths:
              /orders/{id}:
                get: { operationId: getOrder, summary: One order }
            """
        }));
        Assert.Equal("get_order", imported.GetProperty("endpoints")[0].GetProperty("name").GetString());

        var updated = await Json(await api.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/connections/{connectionId}",
            new { settings = new Dictionary<string, string> { ["endpoints"] = imported.GetProperty("endpoints").GetRawText() } }));
        Assert.Contains("store__get_order", updated.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));

        // Invalid endpoints are refused and the working ones stay.
        var bad = await api.PatchAsJsonAsync($"/api/workspaces/{workspaceId}/connections/{connectionId}",
            new { settings = new Dictionary<string, string> { ["endpoints"] = """[{"name":"get","path":"/x"}]""" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var still = (await Json(await api.GetAsync($"/api/workspaces/{workspaceId}/connections"))).EnumerateArray().Single();
        Assert.Contains("store__get_order", still.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));

        var notOpenApi = await api.PostAsJsonAsync("/api/integrations/openapi", new { spec = "{\"hello\":1}" });
        Assert.Equal(HttpStatusCode.BadRequest, notOpenApi.StatusCode);
        output.WriteLine(await notOpenApi.Content.ReadAsStringAsync());
    }
}
