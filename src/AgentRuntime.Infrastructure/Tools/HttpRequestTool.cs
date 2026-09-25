using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>Outbound HTTP for agents. Credentials never come from the agent — only a URL/method/body do.</summary>
public sealed class HttpRequestTool(IHttpClientFactory httpClientFactory, IOptions<ToolsOptions> options) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "http_request",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Make an outbound HTTP GET or POST request to a public URL.",
        RequiredPermissions = ToolPermission.NetworkAccess,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "method": { "type": "string", "enum": ["GET", "POST"] },
            "url": { "type": "string" },
            "body": { "type": "string" }
          },
          "required": ["method", "url"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<HttpArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid http_request arguments.");

        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return ToolExecutionResult.Fail("url must be an absolute http(s) URL.");
        }

        var client = httpClientFactory.CreateClient("agent-tools");
        client.Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds);

        using var httpRequest = new HttpRequestMessage(new HttpMethod(args.Method), uri);
        if (args.Body is not null && args.Method == "POST")
        {
            httpRequest.Content = new StringContent(args.Body);
        }

        using var response = await client.SendAsync(httpRequest, request.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(request.CancellationToken);
        if (body.Length > options.Value.HttpMaxResponseBytes)
        {
            body = body[..options.Value.HttpMaxResponseBytes] + "...(truncated)";
        }

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            statusCode = (int)response.StatusCode,
            body
        }, ToolJson.Options));
    }

    private sealed record HttpArgs(string Method, string Url, string? Body);
}
