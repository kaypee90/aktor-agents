using System.Text.Json;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace ExamplePlugin;

/// <summary>
/// Example plugin: gives agents a read-only "current weather" tool backed by the free Open-Meteo
/// API (no key). Shows the whole contract: a manifest with settings, validation, tool listing with
/// an honest side-effect class, and execution. Constructor parameters come from DI.
/// </summary>
public sealed class WeatherPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "example-weather",
        Name = "Weather (example plugin)",
        Description = "Current weather for any coordinates, via Open-Meteo.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "units", Label = "Units", Options = ["celsius", "fahrenheit"], DefaultValue = "celsius" }
        ]
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(ConnectionCheck.Success("Ready (no API key needed)."));

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "current",
                Description = "Current temperature and wind at a latitude/longitude.",
                // Reading data changes nothing, so the runtime may safely re-run it after a crash.
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """
                {
                  "type": "object",
                  "properties": { "latitude": { "type": "number" }, "longitude": { "type": "number" } },
                  "required": ["latitude", "longitude"]
                }
                """
            }
        ]);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        if (toolName != "current") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");

        using var args = JsonDocument.Parse(request.ArgumentsJson);
        var lat = args.RootElement.GetProperty("latitude").GetDouble();
        var lon = args.RootElement.GetProperty("longitude").GetDouble();
        var unit = connection.Setting("units", "celsius");

        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,wind_speed_10m&temperature_unit={unit}");
        using var response = await httpClientFactory.CreateClient().GetAsync(url, request.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(request.CancellationToken);
        return response.IsSuccessStatusCode ? ToolExecutionResult.Ok(body) : ToolExecutionResult.Fail($"Open-Meteo returned {(int)response.StatusCode}.");
    }
}
