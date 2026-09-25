using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Integrations;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Connects any Model Context Protocol server (docs/plugins.md), using the official MCP C# SDK.
/// Its tools become workspace tools; each tool's MCP annotations decide its side-effect class:
/// readOnlyHint becomes ReadOnly, idempotentHint becomes Idempotent, and anything else is
/// NonIdempotent (the safe default for crash recovery).
///
/// Transports: <c>http</c> (Streamable HTTP, falling back to SSE) for hosted servers, and
/// <c>stdio</c> for servers launched as a command, which always runs inside a throwaway,
/// resource-limited Docker container and only when Integrations:AllowStdioMcp is enabled.
/// </summary>
public sealed class McpPlugin(
    IHttpClientFactory httpClientFactory,
    IOptions<IntegrationsOptions> options,
    ILoggerFactory loggerFactory) : IToolProviderPlugin, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<McpPlugin>();

    public PluginManifest Manifest { get; } = new()
    {
        Id = "mcp",
        Name = "MCP server",
        Description = "Connect any Model Context Protocol server — Shopify, GitHub, databases, internal tools — and give your agents its tools.",
        Category = PluginCategory.Tools,
        Settings =
        [
            new() { Key = "transport", Label = "Transport", Options = ["http", "stdio"], DefaultValue = "http", Required = true },
            new() { Key = "url", Label = "Server URL (http)", Placeholder = "https://example.com/mcp" },
            new() { Key = "auth_header_name", Label = "Auth header name (http)", DefaultValue = "Authorization" },
            new() { Key = "auth_header_value", Label = "Auth header value (http)", Secret = true, Placeholder = "Bearer …" },
            new() { Key = "command", Label = "Command (stdio, runs in Docker)", Placeholder = "npx -y @modelcontextprotocol/server-everything" },
            new() { Key = "image", Label = "Docker image (stdio)", DefaultValue = "node:22-alpine" },
            new() { Key = "env", Label = "Environment variables (stdio), one KEY=VALUE per line", Secret = true }
        ],
        SetupHelp = "For hosted servers use http with the server's URL (and a token if it needs one). stdio servers " +
                    "(e.g. 'npx -y some-mcp-server') run in an isolated Docker container and must be enabled by the operator " +
                    "(Integrations:AllowStdioMcp)."
    };

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(connection, cancellationToken);
        var info = client.ServerInfo;
        return ConnectionCheck.Success(info is null ? "Connected." : $"Connected to {info.Name} {info.Version}.");
    }

    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(connection, cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => new ToolDefinition
        {
            Name = t.Name,
            Description = string.IsNullOrWhiteSpace(t.Description) ? t.Title ?? t.Name : t.Description,
            JsonSchema = t.JsonSchema.ValueKind == JsonValueKind.Undefined ? """{"type":"object","properties":{}}""" : t.JsonSchema.GetRawText(),
            SideEffects = Classify(t.ProtocolTool.Annotations)
        }).ToList();
    }

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        var arguments = ParseArguments(request.ArgumentsJson);
        CallToolResult result;
        try
        {
            var client = await GetClientAsync(connection, request.CancellationToken);
            result = await client.CallToolAsync(toolName, arguments, cancellationToken: request.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dropped session (server restarted, idle timeout): reconnect once.
            _logger.LogInformation(ex, "MCP call to {Connection}.{Tool} failed; reconnecting", connection.Name, toolName);
            await EvictAsync(connection);
            var client = await GetClientAsync(connection, request.CancellationToken);
            result = await client.CallToolAsync(toolName, arguments, cancellationToken: request.CancellationToken);
        }

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        if (result.IsError == true)
        {
            return ToolExecutionResult.Fail(string.IsNullOrWhiteSpace(text) ? $"{toolName} reported an error." : text);
        }

        var payload = result.StructuredContent is { } structured
            ? JsonSerializer.Serialize(new { result = structured, text = string.IsNullOrWhiteSpace(text) ? null : text })
            : JsonSerializer.Serialize(new { text });
        return ToolExecutionResult.Ok(payload);
    }

    public static ToolSideEffects Classify(ToolAnnotations? annotations) =>
        annotations?.ReadOnlyHint == true ? ToolSideEffects.ReadOnly
        : annotations?.IdempotentHint == true ? ToolSideEffects.Idempotent
        : ToolSideEffects.NonIdempotent;

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind != JsonValueKind.Object
            ? []
            : doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    /// <summary>One live client per connection (and per settings version, so an edited
    /// connection reconnects), shared by all of the workspace's agents.</summary>
    private Task<McpClient> GetClientAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var key = CacheKey(connection);
        var lazy = _clients.GetOrAdd(key, _ => new Lazy<Task<McpClient>>(() => ConnectAsync(connection)));
        return lazy.Value.WaitAsync(cancellationToken).ContinueWith(async t =>
        {
            if (t.IsCompletedSuccessfully) return t.Result;
            _clients.TryRemove(key, out _);
            return await t; // rethrow the connection error
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task<McpClient> ConnectAsync(PluginConnection connection)
    {
        var transport = connection.Setting("transport", "http").ToLowerInvariant() switch
        {
            "stdio" => CreateStdioTransport(connection),
            _ => CreateHttpTransport(connection)
        };

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "aktor-agents", Version = "1.0.0" }
        }, loggerFactory);
    }

    private IClientTransport CreateHttpTransport(PluginConnection connection)
    {
        var url = connection.Setting("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("An http MCP connection needs a valid server URL.");
        }

        var headers = new Dictionary<string, string>();
        if (connection.Secrets.TryGetValue("auth_header_value", out var token) && !string.IsNullOrWhiteSpace(token))
        {
            headers[connection.Setting("auth_header_name", "Authorization")] = token;
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = connection.Name,
            AdditionalHeaders = headers,
            TransportMode = HttpTransportMode.AutoDetect
        }, httpClientFactory.CreateClient("integrations"), loggerFactory, ownsHttpClient: true);
    }

    private IClientTransport CreateStdioTransport(PluginConnection connection)
    {
        var opts = options.Value;
        if (!opts.AllowStdioMcp)
        {
            throw new InvalidOperationException(
                "stdio MCP servers are disabled on this server. An operator can enable them with Integrations:AllowStdioMcp=true " +
                "(they run in isolated Docker containers).");
        }

        var command = connection.Setting("command");
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("A stdio MCP connection needs a command.");

        // Secrets reach the container as environment variables named on the command line but
        // valued only in the docker CLI's own environment, so they never show up in `ps`.
        var env = new Dictionary<string, string?>();
        var args = new List<string>
        {
            "run", "-i", "--rm",
            "--memory", opts.StdioMcpMemoryLimit, "--cpus", opts.StdioMcpCpuLimit, "--pids-limit", "256",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "-e", "HOME=/tmp", "--tmpfs", "/tmp"
        };
        if (connection.Secrets.TryGetValue("env", out var envBlock))
        {
            foreach (var line in envBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var name = line[..eq].Trim();
                env[name] = line[(eq + 1)..];
                args.Add("-e");
                args.Add(name);
            }
        }

        args.Add(connection.Setting("image", "node:22-alpine"));
        args.AddRange(["sh", "-c", command]);

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = connection.Name,
            Command = "docker",
            Arguments = args,
            EnvironmentVariables = env
        }, loggerFactory);
    }

    private async Task EvictAsync(PluginConnection connection)
    {
        if (_clients.TryRemove(CacheKey(connection), out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
        {
            await lazy.Value.Result.DisposeAsync();
        }
    }

    private static string CacheKey(PluginConnection c)
    {
        var fingerprint = string.Join("|", c.Settings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
                          + "|" + string.Join("|", c.Secrets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..16];
        return $"{c.WorkspaceId}/{c.ConnectionId}/{hash}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) await lazy.Value.Result.DisposeAsync();
        }

        _clients.Clear();
    }
}
