using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The <see cref="ILLMProvider"/> agents call. Calls for an organization with its own model
/// settings (docs/llm-settings.md) go to that provider, model and key; everything else goes to
/// the server's configured provider, exactly as before. Providers are cheap to build per call,
/// so a changed setting applies on the next call.
/// </summary>
public sealed class OrganizationLlmRouter(
    [FromKeyedServices(OrganizationLlmRouter.ServerProviderKey)] ILLMProvider serverProvider,
    ILlmSettingsResolver settings,
    LlmProviderFactory factory) : ILLMProvider
{
    public const string ServerProviderKey = "server";

    public string ProviderName => serverProvider.ProviderName;

    public async Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var options = await settings.ResolveAsync(request.TenantId, request.ModelProfileId, cancellationToken);
        // The resolver hands back the server's own options when the organization chose nothing.
        if (ReferenceEquals(options, factory.ServerOptions)) return await serverProvider.CompleteAsync(request, cancellationToken);

        return await factory.Create(options).CompleteAsync(request, cancellationToken);
    }
}

/// <summary>Builds a provider for a given set of options, with an HTTP client for its address.</summary>
public sealed class LlmProviderFactory(IHttpClientFactory httpClients, IOptions<LlmOptions> serverOptions, HeuristicMockLlmProvider mock)
{
    /// <summary>For addresses an organization chose: public internet only, unless the server allows private ones.</summary>
    public const string PublicClientName = "llm-org";
    public const string PrivateClientName = "llm-org-private";

    public LlmOptions ServerOptions => serverOptions.Value;

    public ILLMProvider Create(LlmOptions o)
    {
        var wrapped = Options.Create(o);
        return o.Provider switch
        {
            "Anthropic" => new AnthropicProvider(Client(o, "https://api.anthropic.com/"), wrapped),
            "OpenAI" => new OpenAIProvider(Client(o, "https://api.openai.com/"), wrapped),
            "Gemini" => new GeminiProvider(Client(o, "https://generativelanguage.googleapis.com/"), wrapped),
            "Ollama" => new OllamaProvider(Client(o, DefaultOllamaUrl()), wrapped),
            _ => mock
        };
    }

    public HttpClient Client(LlmOptions o, string defaultBaseUrl)
    {
        var client = httpClients.CreateClient(serverOptions.Value.AllowPrivateBaseUrls ? PrivateClientName : PublicClientName);
        var baseUrl = string.IsNullOrWhiteSpace(o.BaseUrl) ? defaultBaseUrl : o.BaseUrl.Trim();
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, o.TimeoutSeconds));
        return client;
    }

    /// <summary>Ollama on this machine; inside a container, "localhost" is the container itself,
    /// so the Docker host instead (the .NET base images set DOTNET_RUNNING_IN_CONTAINER).</summary>
    public static string DefaultOllamaUrl() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? "http://host.docker.internal:11434/"
            : "http://localhost:11434/";
}

/// <summary>Checks model settings before they're saved: lists the provider's models and makes one
/// tiny call, so a wrong key, model or address shows up in the dashboard rather than mid-task.</summary>
public sealed class LlmConnectionTester(LlmProviderFactory factory)
{
    public sealed record TestResult(bool Ok, string Message, long LatencyMs, int? InputTokens, int? OutputTokens);

    public async Task<TestResult> TestAsync(LlmOptions o, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 15, 120)));
            var response = await factory.Create(o).CompleteAsync(new LlmCompletionRequest
            {
                Messages = [ChatMessage.System("You are a connection check."), ChatMessage.User("Reply with the single word OK.")],
                Model = o.Model,
                MaxTokens = 16,
                Temperature = 0
            }, timeout.Token);
            var reply = response.Content?.Trim() ?? string.Empty;
            return new TestResult(true,
                reply.Length == 0 ? "The model answered (with no text)." : $"The model answered: \"{(reply.Length > 80 ? reply[..80] + "…" : reply)}\"",
                watch.ElapsedMilliseconds, response.InputTokens, response.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TestResult(false, Describe(ex), watch.ElapsedMilliseconds, null, null);
        }
    }

    /// <summary>The provider's model ids, newest naming first; empty when it can't be asked.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmOptions o, CancellationToken ct)
    {
        if (o.Provider == "Mock") return ["mock"];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var (defaultUrl, path) = o.Provider switch
        {
            "Anthropic" => ("https://api.anthropic.com/", "v1/models?limit=100"),
            "OpenAI" => ("https://api.openai.com/", "v1/models"),
            "Gemini" => ("https://generativelanguage.googleapis.com/", "v1beta/models?pageSize=200"),
            "Ollama" => (LlmProviderFactory.DefaultOllamaUrl(), "api/tags"),
            _ => throw new InvalidOperationException($"Unknown provider '{o.Provider}'.")
        };
        var client = factory.Client(o, defaultUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        switch (o.Provider)
        {
            case "Anthropic":
                request.Headers.Add("x-api-key", o.ApiKey ?? string.Empty);
                request.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case "OpenAI":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey ?? string.Empty);
                break;
            case "Gemini":
                request.Headers.Add("x-goog-api-key", o.ApiKey ?? string.Empty);
                break;
        }

        using var response = await client.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{o.Provider} answered {(int)response.StatusCode}: {Clip(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        IEnumerable<string> ids = o.Provider switch
        {
            "Ollama" => Items(root, "models").Select(m => Str(m, "name")),
            "Gemini" => Items(root, "models")
                .Where(m => !m.TryGetProperty("supportedGenerationMethods", out var methods)
                            || methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                .Select(m => Str(m, "name").Replace("models/", string.Empty)),
            _ => Items(root, "data").Select(m => Str(m, "id"))
        };
        return ids.Where(id => id.Length > 0).Distinct().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "No answer in time. Check the address, or that the model is running.",
        HttpRequestException h when (h.InnerException?.Message ?? h.Message).Contains("public address", StringComparison.OrdinalIgnoreCase) =>
            "That address is on a private network, which this server doesn't allow for organization settings.",
        HttpRequestException h => $"Couldn't reach the provider: {Clip(h.Message, 300)}",
        _ => Clip(ex.Message, 400)
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
