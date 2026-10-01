namespace AgentRuntime.Memory;

/// <summary>
/// Turns text into a vector for semantic recall (roadmap P4). Pluggable like the LLM providers:
/// Ollama and OpenAI ship with the runtime, and with none configured memory search falls back to
/// keywords alone. Agents never see this; the memory store calls it.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>False for <see cref="NullEmbeddingProvider"/>: the store then skips embeddings entirely.</summary>
    bool IsConfigured { get; }

    /// <summary>Identifies the vector space ("ollama:nomic-embed-text"). Vectors are only ever
    /// compared with vectors from the same model, so switching models never mixes spaces.</summary>
    string Model { get; }

    /// <summary>The embedding, or null when the text can't be embedded (the caller then stores the
    /// entry without one, and keyword search still finds it).</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool IsConfigured => false;
    public string Model => "none";
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(null);
}

/// <summary>Section "Memory": the embedding provider and how hybrid search ranks results.</summary>
public sealed class MemoryOptions
{
    public const string SectionName = "Memory";

    public EmbeddingOptions Embeddings { get; set; } = new();
    public MemorySearchOptions Search { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary>"None" (default: keyword search only), "Ollama" or "OpenAI".</summary>
    public string Provider { get; set; } = "None";

    /// <summary>e.g. nomic-embed-text (Ollama) or text-embedding-3-small (OpenAI).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Blank: Ollama on its usual address, or api.openai.com.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>OpenAI only. Blank: Llm:ApiKey when the LLM provider is OpenAI too.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Text longer than this is cut before embedding (most models take a few thousand tokens).</summary>
    public int MaxInputCharacters { get; set; } = 8000;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Hybrid ranking: score = vector similarity × VectorWeight + keyword relevance × KeywordWeight +
/// recency × RecencyWeight, where recency halves every <see cref="RecencyHalfLifeDays"/>.
/// </summary>
public sealed class MemorySearchOptions
{
    public double VectorWeight { get; set; } = 0.6;
    public double KeywordWeight { get; set; } = 0.3;
    public double RecencyWeight { get; set; } = 0.1;
    public double RecencyHalfLifeDays { get; set; } = 30;

    /// <summary>Entries less similar than this (cosine, 0–1) only show up if their words match.</summary>
    public double MinSimilarity { get; set; } = 0.35;

    public int MaxResults { get; set; } = 20;
}
