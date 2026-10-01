using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Memory;

/// <summary>Ollama's /api/embed (e.g. nomic-embed-text, mxbai-embed-large): local and free.</summary>
public sealed class OllamaEmbeddingProvider(HttpClient http, IOptions<MemoryOptions> options, ILogger<OllamaEmbeddingProvider> logger) : IEmbeddingProvider
{
    private readonly EmbeddingOptions _opts = options.Value.Embeddings;

    public bool IsConfigured => true;
    public string Model => $"ollama:{ModelName}";
    private string ModelName => string.IsNullOrWhiteSpace(_opts.Model) ? "nomic-embed-text" : _opts.Model;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/embed", new { model = ModelName, input = Clip(text, _opts.MaxInputCharacters) }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.GetProperty("embeddings")[0].EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning("Ollama embedding failed ({Model}): {Error}", ModelName, ex.Message);
            return null;
        }
    }

    internal static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}

/// <summary>OpenAI's /v1/embeddings (e.g. text-embedding-3-small).</summary>
public sealed class OpenAIEmbeddingProvider(HttpClient http, IOptions<MemoryOptions> options, IOptions<Configuration.LlmOptions> llm,
    ILogger<OpenAIEmbeddingProvider> logger) : IEmbeddingProvider
{
    private readonly EmbeddingOptions _opts = options.Value.Embeddings;

    public bool IsConfigured => true;
    public string Model => $"openai:{ModelName}";
    private string ModelName => string.IsNullOrWhiteSpace(_opts.Model) ? "text-embedding-3-small" : _opts.Model;

    private string? ApiKey => !string.IsNullOrWhiteSpace(_opts.ApiKey) ? _opts.ApiKey
        : llm.Value.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ? llm.Value.ApiKey : null;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            logger.LogWarning("OpenAI embeddings are configured without an API key (Memory:Embeddings:ApiKey).");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings")
            {
                Content = JsonContent.Create(new { model = ModelName, input = OllamaEmbeddingProvider.Clip(text, _opts.MaxInputCharacters) })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning("OpenAI embedding failed ({Model}): {Error}", ModelName, ex.Message);
            return null;
        }
    }
}
