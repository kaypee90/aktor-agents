using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentRuntime.Api.Platform;
using AgentRuntime.Configuration;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Every JSON surface in this API — controllers, the SSE stream, and tool schemas/arguments in
// AgentRuntime.Tools.ToolJson — consistently uses snake_case properties and PascalCase enum names
// (e.g. status: "Completed", matching what a few endpoints already produced manually via
// enum.ToString() before this was centralized — without the enum converter, System.Text.Json
// serializes enums as raw numbers by default, which silently breaks any client expecting a string).
var apiJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Aktor Agents API";
    doc.Info.Description = "Authenticate with an API key: 'Authorization: Bearer ak_…' (Settings → API keys). See docs/platform.md.";
    return Task.CompletedTask;
}));

// Platform: every request is authenticated (session cookie or API key) and scoped to one
// organization (docs/platform.md). Secret-URL endpoints (webhooks, channels) opt out explicitly.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantAccess>();
builder.Services.AddAuthentication(AktorAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AktorAuthenticationHandler>(AktorAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(Policies.Add);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Password guessing: a few attempts per minute per client address.
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddAgentRuntimeCore(builder.Configuration);
builder.Services.AddAgentRuntimeInfrastructure(builder.Configuration);

// One task service behind every way in: REST, MCP, A2A and ACP (docs/integrations.md).
builder.Services.AddSingleton<UploadStore>();
builder.Services.AddSingleton<TaskService>();
builder.Services.AddSingleton<TaskPreviewService>();
builder.Services.Configure<AgentRuntime.Api.Interop.A2aSettings>(builder.Configuration.GetSection(AgentRuntime.Api.Interop.A2aSettings.SectionName));
builder.Services.Configure<AgentRuntime.Api.Interop.McpServerSettings>(builder.Configuration.GetSection(AgentRuntime.Api.Interop.McpServerSettings.SectionName));

// Aktor as an MCP server (streamable HTTP at /mcp), authenticated with the same API keys.
// Stateless: every request stands alone (no session affinity), and a tool sees its own request's
// caller; progress notifications still stream back on the call's own response.
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "aktor", Title = "Aktor governed agent teams", Version = "1.0.0" };
        o.ServerInstructions = "Aktor runs autonomous agent teams under budgets the server enforces. Call run_goal to start one, " +
                               "then get_task_status (it can wait) and get_task_result. Every result has a dashboard_url and a correlation_id.";
    })
    .WithHttpTransport(o => o.Stateless = true)
    .AddAuthorizationFilters()
    .WithTools<AgentRuntime.Api.Interop.AktorMcpTools>(AgentRuntime.Api.Interop.McpJson.Options);

// A hiccup in a background service (e.g. the Postgres event writer losing its connection) should
// never take down the whole silo/API; it logs and keeps running rather than stopping the host.
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // The dashboard signs in with a cookie, so its origin may send credentials.
        .AllowCredentials());
});

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("AktorAgents")
        .AddConsoleExporter());

// Orleans co-hosted silo (CLAUDE.md section 7). Grain state and reminders are durable in Postgres
// by default (docs/durability.md); set Silo:Clustering=AdoNet to run several silos that take
// over each other's agents, or Silo:Storage=Memory for a throwaway demo.
builder.Host.UseOrleans(silo =>
{
    silo.UseAgentRuntimeStorage(builder.Configuration);
    silo.ConfigureLogging(logging => logging.AddConsole());
});

var app = builder.Build();

await app.MigrateDatabaseAsync();

// The API description is public: it's what SDKs and integrators build against.
app.MapOpenApi().AllowAnonymous();

app.UseCors();
// ACP (/acp) runs over a WebSocket; keep-alives stop proxies closing a long prompt's connection.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapMcp("/mcp");
// Aktor as an A2A agent and an ACP agent (docs/integrations.md).
AgentRuntime.Api.Interop.A2aEndpoint.MapA2a(app);
AgentRuntime.Api.Interop.AcpEndpoint.MapAcp(app);

// Real-time event stream (CLAUDE.md sections 29, 31, 46) via Server-Sent Events.
app.MapGet("/ws/events", async (HttpContext http, IEventStream stream, string? taskId, CancellationToken ct) =>
{
    // Each viewer sees their own organization's events only.
    var tenant = http.Caller().TenantId;
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    await foreach (var evt in stream.Subscribe(ct))
    {
        if (!AgentRuntime.Tenancy.TenantIds.Same(evt.TenantId, tenant) ||
            (taskId is not null && !string.Equals(evt.TaskId, taskId, StringComparison.Ordinal)))
        {
            continue;
        }

        var json = JsonSerializer.Serialize(evt, apiJsonOptions);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

app.Run();

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
