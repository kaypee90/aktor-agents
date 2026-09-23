using System.Text.Json;
using System.Text.Json.Serialization;
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
builder.Services.AddOpenApi();

builder.Services.AddAgentRuntimeCore(builder.Configuration);
builder.Services.AddAgentRuntimeInfrastructure(builder.Configuration);

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
        .AllowAnyMethod());
});

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("AktorAgents")
        .AddConsoleExporter());

// Orleans co-hosted silo (CLAUDE.md section 7): a single-process silo is sufficient for this
// prototype. Swap UseLocalhostClustering/AddMemoryGrainStorage for the AdoNet providers already
// referenced in AgentRuntime.Infrastructure to run a multi-silo production cluster against Postgres.
builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");
    silo.ConfigureLogging(logging => logging.AddConsole());
});

var app = builder.Build();

await app.MigrateDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapControllers();

// Real-time event stream (CLAUDE.md sections 29, 31, 46) via Server-Sent Events.
app.MapGet("/ws/events", async (HttpContext http, IEventStream stream, string? taskId, CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    await foreach (var evt in stream.Subscribe(ct))
    {
        if (taskId is not null && !string.Equals(evt.TaskId, taskId, StringComparison.Ordinal))
        {
            continue;
        }

        var json = JsonSerializer.Serialize(evt, apiJsonOptions);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

app.Run();
