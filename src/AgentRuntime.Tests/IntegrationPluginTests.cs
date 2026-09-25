using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Integrations;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentRuntime.Tests;

public class IntegrationPluginTests
{
    private sealed class StubHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PluginConnection Connection(Dictionary<string, string> settings, Dictionary<string, string>? secrets = null, string? inboundSecret = null, string? inboundUrl = null) => new()
    {
        ConnectionId = "conn-1",
        WorkspaceId = "ws-1",
        Name = "shop",
        Settings = settings,
        Secrets = secrets ?? [],
        InboundSecret = inboundSecret,
        InboundUrl = inboundUrl
    };

    private static ToolExecutionRequest Request(string tool, object args, string key = "agent-1:call-1") => new()
    {
        ToolName = tool,
        AgentId = "agent-1",
        TaskId = "ws-1",
        ArgumentsJson = JsonSerializer.Serialize(args),
        IdempotencyKey = key
    };

    // ---- Vault ---------------------------------------------------------------

    [Fact]
    public void SecretProtector_RoundTrips_AndBindsCiphertextToItsScopeAndKey()
    {
        var protector = new SecretProtector(RandomNumberGenerator.GetBytes(32));
        var stored = protector.Protect("shpat_super_secret", "ws-1/conn-1", "auth_header_value");

        Assert.DoesNotContain("shpat", Encoding.UTF8.GetString(stored));
        Assert.Equal("shpat_super_secret", protector.Unprotect(stored, "ws-1/conn-1", "auth_header_value"));
        // A row copied onto another connection or field doesn't decrypt.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored, "ws-2/conn-9", "auth_header_value"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored, "ws-1/conn-1", "other"));
        stored[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored, "ws-1/conn-1", "auth_header_value"));
    }

    [Fact]
    public void SecretProtector_UsesAFreshNonceEachTime() =>
        Assert.NotEqual(
            new SecretProtector(new byte[32]).Protect("x", "s", "k"),
            new SecretProtector(new byte[32]).Protect("x", "s", "k"));

    // ---- Naming ----------------------------------------------------------------

    [Theory]
    [InlineData("My Shopify Store!", "my_shopify_store")]
    [InlineData("a__b", "a_b")]
    [InlineData("   ", "connection")]
    public void ConnectionNames_AreSafeSlugs(string input, string expected) => Assert.Equal(expected, ConnectionNames.Slug(input));

    [Fact]
    public void ExposedToolNames_FitProviderRules()
    {
        var name = ConnectionNames.Expose("shop", "get.inventory/levels");
        Assert.Equal("shop__get_inventory_levels", name);
        Assert.True(ConnectionNames.IsConnectionTool(name));
        Assert.True(ConnectionNames.Expose(new string('c', 24), new string('t', 80)).Length <= 64);
    }

    // ---- HTTP API ----------------------------------------------------------------

    [Theory]
    [InlineData("https://evil.example.com/steal")]
    [InlineData("//evil.example.com/steal")]
    [InlineData("../../oauth/access_token")]
    [InlineData("/%2e%2e/%2e%2e/secret")]
    [InlineData("..\\..\\secret")]
    public void HttpApi_RefusesPathsThatLeaveTheBaseUrl(string path)
    {
        var c = Connection(new() { ["base_url"] = "https://shop.myshopify.com/admin/api/2025-07" });
        Assert.False(HttpApiPlugin.TryResolve(c, path, default, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task HttpApi_GetSendsTheStoredCredential_AndOnlyToTheBaseHost()
    {
        var handler = new StubHandler(body: """{"products":[]}""");
        var plugin = new HttpApiPlugin(new StubFactory(handler));
        var c = Connection(
            new() { ["base_url"] = "https://shop.myshopify.com/admin/api/2025-07", ["auth_header_name"] = "X-Shopify-Access-Token" },
            new() { ["auth_header_value"] = "shpat_123" });

        var result = await plugin.ExecuteToolAsync(c, "get", Request("shop__get", new { path = "/products.json", query = new { limit = "5" } }));

        Assert.True(result.Success);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal("https://shop.myshopify.com/admin/api/2025-07/products.json?limit=5", request.RequestUri!.ToString());
        Assert.Equal("shpat_123", request.Headers.GetValues("X-Shopify-Access-Token").Single());
    }

    [Fact]
    public async Task HttpApi_WritesExistOnlyWhenEnabled_AndCarryTheIdempotencyKey()
    {
        var handler = new StubHandler();
        var plugin = new HttpApiPlugin(new StubFactory(handler));
        var readOnly = Connection(new() { ["base_url"] = "https://api.example.com/v1" });
        var writable = Connection(new() { ["base_url"] = "https://api.example.com/v1", ["allow_writes"] = "true" });

        Assert.Equal(["get"], (await plugin.ListToolsAsync(readOnly, default)).Select(t => t.Name));
        Assert.False((await plugin.ExecuteToolAsync(readOnly, "send", Request("x", new { method = "POST", path = "/orders" }))).Success);

        var tools = await plugin.ListToolsAsync(writable, default);
        Assert.Equal(ToolSideEffects.ReadOnly, tools.Single(t => t.Name == "get").SideEffects);
        Assert.Equal(ToolSideEffects.NonIdempotent, tools.Single(t => t.Name == "send").SideEffects);

        await plugin.ExecuteToolAsync(writable, "send", Request("x", new { method = "POST", path = "/orders", body = new { qty = 1 } }, key: "agent-1:call-7"));
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("agent-1:call-7", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("""{"qty":1}""", body);
    }

    // ---- Twilio ----------------------------------------------------------------

    private static string TwilioSignature(string token, string url, IDictionary<string, string> form)
    {
        var data = url + string.Concat(form.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + kv.Value));
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(data)));
    }

    [Fact]
    public async Task Twilio_InboundRequiresAValidSignature_WhenThePublicUrlIsKnown()
    {
        var plugin = new TwilioSmsPlugin(new StubFactory(new StubHandler()));
        const string url = "https://agents.example.com/api/channels/ws-1/conn-1/abc";
        var c = Connection(new() { ["account_sid"] = "AC1", ["from_number"] = "+15550001111" }, new() { ["auth_token"] = "tok" }, inboundUrl: url);
        var form = new Dictionary<string, string> { ["From"] = "+15557654321", ["Body"] = "pause the monitor", ["MessageSid"] = "SM123" };
        var body = string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var ok = await plugin.ParseInboundAsync(c, new InboundRequest
        {
            Body = body,
            PublicUrl = url,
            Headers = new Dictionary<string, string> { ["X-Twilio-Signature"] = TwilioSignature("tok", url, form) }
        }, default);
        Assert.True(ok.IsMessage);
        Assert.Equal("+15557654321", ok.SenderId);
        Assert.Equal("pause the monitor", ok.Text);
        Assert.Equal("SM123", ok.MessageId);
        Assert.Contains("<Response>", ok.ResponseBody);

        var forged = await plugin.ParseInboundAsync(c, new InboundRequest
        {
            Body = body,
            PublicUrl = url,
            Headers = new Dictionary<string, string> { ["X-Twilio-Signature"] = TwilioSignature("wrong", url, form) }
        }, default);
        Assert.True(forged.Rejected);
    }

    [Fact]
    public async Task Twilio_NotificationFailures_DistinguishRetryableFromPermanent()
    {
        var c = Connection(new() { ["account_sid"] = "AC1", ["from_number"] = "+1", ["notify_to"] = "+2" }, new() { ["auth_token"] = "tok" });
        var n = new OutboundNotification { DeliveryId = "d1", WorkspaceName = "Shop", AuthorName = "Monitor", Text = "Low stock", Urgency = "urgent" };

        var permanent = await new TwilioSmsPlugin(new StubFactory(new StubHandler(HttpStatusCode.BadRequest))).SendNotificationAsync(c, n, default);
        var transient = await new TwilioSmsPlugin(new StubFactory(new StubHandler(HttpStatusCode.ServiceUnavailable))).SendNotificationAsync(c, n, default);
        var handler = new StubHandler(HttpStatusCode.Created);
        var sent = await new TwilioSmsPlugin(new StubFactory(handler)).SendNotificationAsync(c, n, default);

        Assert.False(permanent.Retryable);
        Assert.True(transient.Retryable);
        Assert.True(sent.Delivered);
        Assert.Contains("Low+stock", handler.Requests.Single().Body);
    }

    // ---- Telegram ------------------------------------------------------------------

    [Fact]
    public async Task Telegram_InboundNeedsTheSecretToken_AndIgnoresBots()
    {
        var plugin = new TelegramPlugin(new StubFactory(new StubHandler()));
        var c = Connection([], new() { ["bot_token"] = "123:ABC" }, inboundSecret: "s3cret");
        const string update = """{"update_id":77,"message":{"chat":{"id":4242},"from":{"is_bot":false,"first_name":"Kofi"},"text":"status?"}}""";

        var ok = await plugin.ParseInboundAsync(c, new InboundRequest
        {
            Body = update,
            Headers = new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = "s3cret" }
        }, default);
        Assert.True(ok.IsMessage);
        Assert.Equal("4242", ok.SenderId);
        Assert.Equal("status?", ok.Text);
        Assert.Equal("77", ok.MessageId);

        Assert.True((await plugin.ParseInboundAsync(c, new InboundRequest { Body = update }, default)).Rejected);

        var fromBot = await plugin.ParseInboundAsync(c, new InboundRequest
        {
            Body = update.Replace("\"is_bot\":false", "\"is_bot\":true"),
            Headers = new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = "s3cret" }
        }, default);
        Assert.False(fromBot.IsMessage);
    }

    // ---- MCP ---------------------------------------------------------------------

    [Fact]
    public void Mcp_AnnotationsMapToSideEffectClasses()
    {
        Assert.Equal(ToolSideEffects.ReadOnly, McpPlugin.Classify(new ToolAnnotations { ReadOnlyHint = true }));
        Assert.Equal(ToolSideEffects.Idempotent, McpPlugin.Classify(new ToolAnnotations { IdempotentHint = true }));
        Assert.Equal(ToolSideEffects.NonIdempotent, McpPlugin.Classify(new ToolAnnotations { DestructiveHint = true }));
        // No annotations: assume the worst for crash recovery.
        Assert.Equal(ToolSideEffects.NonIdempotent, McpPlugin.Classify(null));
    }
}
