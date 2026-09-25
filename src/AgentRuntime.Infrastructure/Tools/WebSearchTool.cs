using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Web search backed by a configurable provider (Tools:SearchProvider — Tavily or Brave). Without
/// an API key it returns a clear "unavailable" result rather than fabricating results.
/// Results are reduced to title/url/snippet: whatever this returns is resent to the LLM on every
/// later turn of the agent's conversation, so raw provider JSON would burn token budget fast.
/// </summary>
public sealed class WebSearchTool(IHttpClientFactory httpClientFactory, IOptions<ToolsOptions> options) : ITool
{
    private const int MaxSnippetLength = 500;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "web_search",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Search the public web for current information. Returns a short list of results " +
                      "(title, url, snippet) and, when available, a brief summary answer.",
        RequiredPermissions = ToolPermission.NetworkAccess,
        JsonSchema = """{ "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<SearchArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid web_search arguments.");
        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return ToolExecutionResult.Fail("web_search requires a non-empty query.");
        }

        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.SearchApiKey))
        {
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                available = false,
                message = "Web search is not configured (no search API key). Do not retry or spawn another " +
                          "agent to search; rely on existing knowledge and note the limitation."
            }, ToolJson.Options));
        }

        var client = httpClientFactory.CreateClient("agent-tools");
        client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        var maxResults = Math.Clamp(opts.SearchMaxResults, 1, 20);

        return opts.SearchProvider.ToLowerInvariant() switch
        {
            "tavily" => await SearchTavilyAsync(client, opts, args.Query, maxResults, request.CancellationToken),
            "brave" => await SearchBraveAsync(client, opts, args.Query, maxResults, request.CancellationToken),
            _ => ToolExecutionResult.Fail($"Unknown Tools:SearchProvider '{opts.SearchProvider}' (expected Tavily or Brave).")
        };
    }

    private static async Task<ToolExecutionResult> SearchTavilyAsync(
        HttpClient client, ToolsOptions opts, string query, int maxResults, CancellationToken ct)
    {
        // Tavily: POST JSON with a Bearer key (the older api_key-in-body form is deprecated).
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointOr(opts, "https://api.tavily.com/search"))
        {
            Content = JsonContent.Create(new
            {
                query,
                max_results = maxResults,
                search_depth = "basic",
                include_answer = true
            })
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.SearchApiKey);

        using var response = await client.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return ToolExecutionResult.Fail($"Tavily search error {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var results = root.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(r => new SearchResult(
                GetString(r, "title"), GetString(r, "url"), Truncate(GetString(r, "content"), MaxSnippetLength))).ToList()
            : [];
        var answer = GetString(root, "answer");

        return Ok(results, string.IsNullOrWhiteSpace(answer) ? null : answer);
    }

    private static async Task<ToolExecutionResult> SearchBraveAsync(
        HttpClient client, ToolsOptions opts, string query, int maxResults, CancellationToken ct)
    {
        var baseUrl = EndpointOr(opts, "https://api.search.brave.com/res/v1/web/search");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}?q={Uri.EscapeDataString(query)}&count={maxResults}");
        // Per-request header: the named client is shared by concurrent agents.
        httpRequest.Headers.Add("X-Subscription-Token", opts.SearchApiKey);

        using var response = await client.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return ToolExecutionResult.Fail($"Brave search error {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var results = doc.RootElement.TryGetProperty("web", out var web) &&
                      web.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Take(maxResults).Select(r => new SearchResult(
                GetString(r, "title"), GetString(r, "url"), Truncate(GetString(r, "description"), MaxSnippetLength))).ToList()
            : [];

        return Ok(results, answer: null);
    }

    private static ToolExecutionResult Ok(List<SearchResult> results, string? answer) =>
        ToolExecutionResult.Ok(JsonSerializer.Serialize(new { available = true, answer, results }, ToolJson.Options));

    // Blank-as-unset, not just null: config files commonly carry "" placeholders.
    private static string EndpointOr(ToolsOptions opts, string fallback) =>
        string.IsNullOrWhiteSpace(opts.SearchApiUrl) ? fallback : opts.SearchApiUrl;

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private sealed record SearchArgs(string Query);

    private sealed record SearchResult(string Title, string Url, string Snippet);
}
