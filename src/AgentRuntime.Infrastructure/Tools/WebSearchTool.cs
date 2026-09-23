using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Web search backed by a configurable search API. Without an API key configured, it returns a
/// clear "unavailable" result rather than fabricating search results.
/// </summary>
public sealed class WebSearchTool(IHttpClientFactory httpClientFactory, IOptions<ToolsOptions> options) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "web_search",
        Description = "Search the public web for current information.",
        RequiredPermissions = ToolPermission.NetworkAccess,
        JsonSchema = """{ "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<SearchArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid web_search arguments.");

        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.SearchApiKey))
        {
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                available = false,
                message = "Web search is not configured (Tools:SearchApiKey is empty). " +
                           "Rely on existing knowledge or ask another agent instead."
            }, ToolJson.Options));
        }

        var client = httpClientFactory.CreateClient("agent-tools");
        client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        client.DefaultRequestHeaders.Remove("X-Subscription-Token");
        client.DefaultRequestHeaders.Add("X-Subscription-Token", opts.SearchApiKey);

        var url = $"{opts.SearchApiUrl}?q={Uri.EscapeDataString(args.Query)}";
        using var response = await client.GetAsync(url, request.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(request.CancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return ToolExecutionResult.Fail($"Search API error {(int)response.StatusCode}: {Truncate(body)}");
        }

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { available = true, results = Truncate(body) }, ToolJson.Options));
    }

    private static string Truncate(string s) => s.Length > 5_000 ? s[..5_000] + "...(truncated)" : s;

    private sealed record SearchArgs(string Query);
}
