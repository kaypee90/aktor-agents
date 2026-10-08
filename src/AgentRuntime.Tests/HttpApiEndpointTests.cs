using System.Net;
using System.Text.Json;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>An HTTP API connection's own endpoints (added by hand or imported from OpenAPI) as typed
/// tools: validation, the schemas agents see, and requests that stay under the base URL.</summary>
public class HttpApiEndpointTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private const string Endpoints = """
    [
      { "name": "get_order", "method": "GET", "path": "/orders/{order_id}", "description": "One order.",
        "params": [ { "name": "expand", "in": "query", "type": "boolean" } ] },
      { "name": "create_refund", "method": "post", "path": "orders/{order_id}/refunds",
        "body_schema": { "type": "object", "properties": { "amount": { "type": "number" } }, "required": ["amount"] } }
    ]
    """;

    private static PluginConnection Connection(bool writes = false, string endpoints = Endpoints) => new()
    {
        ConnectionId = "conn-1",
        WorkspaceId = "ws-1",
        Name = "store",
        Settings = new Dictionary<string, string>
        {
            ["base_url"] = "https://api.example.com/v2",
            ["allow_writes"] = writes ? "true" : "false",
            [HttpApiEndpoints.SettingKey] = endpoints
        },
        Secrets = new Dictionary<string, string> { ["auth_header_value"] = "Bearer sk_live" }
    };

    private static ToolExecutionRequest Call(string tool, object args) => new()
    {
        ToolName = "store__" + tool,
        AgentId = "agent-1",
        TaskId = "ws-1",
        ArgumentsJson = JsonSerializer.Serialize(args),
        IdempotencyKey = "agent-1:call-1"
    };

    [Fact]
    public void Endpoints_are_normalized_and_path_placeholders_become_required_parameters()
    {
        var endpoints = HttpApiEndpoints.Parse(Endpoints, out var errors);

        Assert.Empty(errors);
        var refund = endpoints.Single(e => e.Name == "create_refund");
        Assert.Equal("POST", refund.Method);
        Assert.Equal("/orders/{order_id}/refunds", refund.Path);
        var id = Assert.Single(refund.Params);
        Assert.Equal(("order_id", "path", true), (id.Name, id.In, id.Required));
    }

    [Theory]
    [InlineData("""[{"name":"Get Order","path":"/orders"}]""", "lowercase")]
    [InlineData("""[{"name":"get","path":"/orders"}]""", "reserved")]
    [InlineData("""[{"name":"a","path":"/x"},{"name":"a","path":"/y"}]""", "Two endpoints")]
    [InlineData("""[{"name":"a","method":"TRACE","path":"/x"}]""", "method")]
    [InlineData("""[{"name":"a","path":"/../admin"}]""", "relative")]
    [InlineData("""[{"name":"a","path":"https://evil.example.com/x"}]""", "relative")]
    [InlineData("""[{"name":"a","path":"/x","params":[{"name":"h","in":"header"}]}]""", "path or the query")]
    [InlineData("""[{"name":"a","path":"/x","params":[{"name":"id","in":"path"}]}]""", "isn't in the path")]
    [InlineData("""not json""", "valid JSON")]
    public void Invalid_endpoints_are_refused_with_a_reason(string json, string expected)
    {
        HttpApiEndpoints.Parse(json, out var errors);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Each_endpoint_is_a_typed_tool_and_writes_need_writes_allowed()
    {
        var plugin = new HttpApiPlugin(new StubFactory(new StubHandler()));

        var readOnly = await plugin.ListToolsAsync(Connection(), default);
        Assert.Equal(["get", "get_order"], readOnly.Select(t => t.Name));

        var tools = await plugin.ListToolsAsync(Connection(writes: true), default);
        var getOrder = tools.Single(t => t.Name == "get_order");
        Assert.Equal(ToolSideEffects.ReadOnly, getOrder.SideEffects);
        var schema = JsonDocument.Parse(getOrder.JsonSchema).RootElement;
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("expand").GetProperty("type").GetString());
        Assert.Equal(["order_id"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        var refund = tools.Single(t => t.Name == "create_refund");
        Assert.Equal(ToolSideEffects.NonIdempotent, refund.SideEffects);
        var refundSchema = JsonDocument.Parse(refund.JsonSchema).RootElement;
        Assert.Equal("number", refundSchema.GetProperty("properties").GetProperty("body").GetProperty("properties").GetProperty("amount").GetProperty("type").GetString());
        Assert.Contains("body", refundSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_call_builds_the_request_under_the_base_url_with_the_stored_credential()
    {
        var handler = new StubHandler();
        var plugin = new HttpApiPlugin(new StubFactory(handler));

        var read = await plugin.ExecuteToolAsync(Connection(), "get_order", Call("get_order", new { order_id = "A 17", expand = true }));
        var write = await plugin.ExecuteToolAsync(Connection(writes: true), "create_refund", Call("create_refund", new { order_id = 17, body = new { amount = 5.5 } }));

        Assert.True(read.Success, read.ErrorMessage);
        Assert.True(write.Success, write.ErrorMessage);
        // A JSON response comes back as JSON, not as a string of escaped JSON.
        Assert.True(JsonDocument.Parse(read.ResultJson).RootElement.GetProperty("body").GetProperty("ok").GetBoolean());
        Assert.Equal("https://api.example.com/v2/orders/A%2017?expand=true", handler.Requests[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer sk_live", handler.Requests[0].Request.Headers.Authorization!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Request.Method);
        Assert.Equal("https://api.example.com/v2/orders/17/refunds", handler.Requests[1].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"amount":5.5}""", handler.Requests[1].Body);
        Assert.Equal("agent-1:call-1", handler.Requests[1].Request.Headers.GetValues("Idempotency-Key").Single());
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("1/../../admin")]
    [InlineData("..")]
    [InlineData("a\\b")]
    public async Task A_path_value_cannot_leave_its_segment(string value)
    {
        var handler = new StubHandler();
        var plugin = new HttpApiPlugin(new StubFactory(handler));

        var result = await plugin.ExecuteToolAsync(Connection(), "get_order", Call("get_order", new { order_id = value }));

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Missing_required_parameters_and_disallowed_writes_are_refused()
    {
        var handler = new StubHandler();
        var plugin = new HttpApiPlugin(new StubFactory(handler));

        Assert.Contains("order_id", (await plugin.ExecuteToolAsync(Connection(), "get_order", Call("get_order", new { }))).ErrorMessage);
        Assert.Contains("doesn't allow writes", (await plugin.ExecuteToolAsync(Connection(), "create_refund",
            Call("create_refund", new { order_id = 1, body = new { amount = 1 } }))).ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_endpoints_fail_the_connection_check()
    {
        var plugin = new HttpApiPlugin(new StubFactory(new StubHandler()));
        var check = await plugin.ValidateAsync(Connection(endpoints: """[{"name":"get","path":"/x"}]"""), default);
        Assert.False(check.Ok);
        Assert.Contains("reserved", check.Message);
    }

    // ---- OpenAPI import ------------------------------------------------------------

    private const string OpenApiYaml = """
    openapi: 3.0.3
    info:
      title: Store API
      version: 1.0
    servers:
      - url: https://{region}.store.example.com/v1
        variables:
          region:
            default: eu
    paths:
      /orders/{orderId}:
        parameters:
          - $ref: '#/components/parameters/OrderId'
        get:
          operationId: getOrderById
          summary: Get an order
          parameters:
            - name: expand
              in: query
              schema: { type: boolean }
            - name: X-Request-Id
              in: header
              schema: { type: string }
        delete:
          summary: Cancel an order
      /orders:
        post:
          operationId: createOrder
          requestBody:
            content:
              application/json:
                schema:
                  $ref: '#/components/schemas/NewOrder'
    components:
      parameters:
        OrderId:
          name: orderId
          in: path
          required: true
          schema: { type: integer }
      schemas:
        NewOrder:
          type: object
          example: { sku: A1 }
          properties:
            sku: { type: string }
            quantity: { type: integer }
            parent:
              $ref: '#/components/schemas/NewOrder'
    """;

    [Fact]
    public void OpenApi_yaml_becomes_endpoints_with_refs_inlined()
    {
        var result = OpenApiImport.Parse(OpenApiYaml);

        Assert.Equal("Store API", result.Title);
        Assert.Equal("https://eu.store.example.com/v1", result.BaseUrl);
        Assert.Equal(["get_order_by_id", "delete_orders_order_id", "create_order"], result.Endpoints.Select(e => e.Name));

        var get = result.Endpoints[0];
        Assert.Equal("/orders/{orderId}", get.Path);
        Assert.Equal(["orderId", "expand"], get.Params.Select(p => p.Name));
        Assert.Equal("integer", get.Params[0].Type);
        Assert.Equal("boolean", get.Params[1].Type);
        Assert.Contains(result.Warnings, w => w.Contains("header"));

        var body = result.Endpoints[2].BodySchema!.AsObject();
        Assert.Equal("string", body["properties"]!["sku"]!["type"]!.GetValue<string>());
        Assert.False(body.ContainsKey("example"));
        // The self-reference is cut off instead of recursing forever.
        Assert.Equal("object", body["properties"]!["parent"]!["type"]!.GetValue<string>());

        // What's imported passes the same checks as endpoints typed by hand.
        HttpApiEndpoints.Parse(HttpApiEndpoints.Serialize(result.Endpoints), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Swagger_2_json_reads_body_parameters_and_the_host()
    {
        var result = OpenApiImport.Parse("""
        {
          "swagger": "2.0",
          "host": "legacy.example.com",
          "basePath": "/api",
          "schemes": ["https"],
          "paths": {
            "/tickets": {
              "post": {
                "operationId": "createTicket",
                "parameters": [{ "in": "body", "name": "ticket", "schema": { "type": "object", "properties": { "title": { "type": "string" } } } }]
              }
            }
          }
        }
        """);

        Assert.Equal("https://legacy.example.com/api", result.BaseUrl);
        var ticket = Assert.Single(result.Endpoints);
        Assert.Equal("create_ticket", ticket.Name);
        Assert.Equal("string", ticket.BodySchema!["properties"]!["title"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"hello\": 1 }")]
    [InlineData("openapi: 3.0.0\ninfo: {}")]
    [InlineData("{ not json")]
    public void Documents_that_arent_openapi_are_refused(string text) =>
        Assert.Throws<FormatException>(() => OpenApiImport.Parse(text));
}
