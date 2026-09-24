using System.Net;
using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

public class WebSearchToolTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (WebSearchTool Tool, StubHandler Handler) Create(ToolsOptions options, HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return (new WebSearchTool(new StubFactory(handler), Options.Create(options)), handler);
    }

    private static ToolExecutionRequest Search(string query) => new()
    {
        ToolName = "web_search",
        AgentId = "a1",
        TaskId = "t1",
        ArgumentsJson = JsonSerializer.Serialize(new { query })
    };

    [Fact]
    public async Task Tavily_PostsQueryWithBearerKey_AndReturnsCompactResults()
    {
        var tavilyResponse = """
        {
          "query": "pygame",
          "answer": "Pygame is a Python game library.",
          "results": [
            { "title": "Pygame", "url": "https://pygame.org", "content": "Pygame is a set of Python modules...", "score": 0.9, "raw_content": null }
          ]
        }
        """;
        var (tool, handler) = Create(
            new ToolsOptions { SearchProvider = "Tavily", SearchApiKey = "tvly-test", SearchMaxResults = 3 },
            HttpStatusCode.OK, tavilyResponse);

        var result = await tool.ExecuteAsync(Search("pygame"));

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.tavily.com/search", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tvly-test", handler.Request.Headers.Authorization.Parameter);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("pygame", sent.RootElement.GetProperty("query").GetString());
        Assert.Equal(3, sent.RootElement.GetProperty("max_results").GetInt32());

        using var output = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("Pygame is a Python game library.", output.RootElement.GetProperty("answer").GetString());
        var first = output.RootElement.GetProperty("results")[0];
        Assert.Equal("https://pygame.org", first.GetProperty("url").GetString());
        Assert.False(first.TryGetProperty("score", out _)); // raw provider fields are dropped
    }

    [Fact]
    public async Task Tavily_ErrorStatus_IsReportedAsToolFailure()
    {
        var (tool, _) = Create(new ToolsOptions { SearchProvider = "Tavily", SearchApiKey = "bad" },
            HttpStatusCode.Unauthorized, """{"detail":{"error":"Unauthorized: missing or invalid API key."}}""");

        var result = await tool.ExecuteAsync(Search("anything"));

        Assert.False(result.Success);
        Assert.Contains("401", result.ErrorMessage);
    }

    [Fact]
    public async Task NoKey_ReportsUnavailable_WithoutCallingTheNetwork()
    {
        var (tool, handler) = Create(new ToolsOptions { SearchProvider = "Tavily", SearchApiKey = "" }, HttpStatusCode.OK, "{}");

        var result = await tool.ExecuteAsync(Search("anything"));

        Assert.True(result.Success);
        Assert.Contains("\"available\":false", result.ResultJson);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task BlankEndpointOverride_FallsBackToProviderDefault()
    {
        var (tool, handler) = Create(new ToolsOptions { SearchProvider = "Tavily", SearchApiKey = "k", SearchApiUrl = "" },
            HttpStatusCode.OK, """{"results":[]}""");

        await tool.ExecuteAsync(Search("q"));

        Assert.Equal("https://api.tavily.com/search", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Brave_StillWorks_WithPerRequestHeader()
    {
        var braveResponse = """{"web":{"results":[{"title":"T","url":"https://x.com","description":"D"}]}}""";
        var (tool, handler) = Create(new ToolsOptions { SearchProvider = "Brave", SearchApiKey = "brave-key" },
            HttpStatusCode.OK, braveResponse);

        var result = await tool.ExecuteAsync(Search("q"));

        Assert.True(result.Success);
        Assert.Equal("brave-key", handler.Request!.Headers.GetValues("X-Subscription-Token").Single());
        Assert.Contains("https://x.com", result.ResultJson);
    }
}
