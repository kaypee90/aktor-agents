using System.Text.RegularExpressions;
using AgentRuntime.Configuration;

namespace AgentRuntime.LLM;

/// <summary>A model's list price in USD per million tokens, as the provider publishes it.</summary>
/// <param name="CachedInputPerMillion">Price of a cached input token; null when the provider lists
/// no cached rate for the model (it is then billed as normal input).</param>
/// <param name="Note">What the list price doesn't cover, e.g. a higher rate for long prompts.</param>
public sealed record ModelPrice(
    string Provider,
    string Model,
    string Name,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CachedInputPerMillion = null,
    string? Note = null)
{
    public decimal InputPerToken => InputPerMillion / 1_000_000m;
    public decimal OutputPerToken => OutputPerMillion / 1_000_000m;

    /// <summary>Cached input price relative to normal input, as <see cref="LlmOptions.CachedInputPriceFactor"/> takes it.</summary>
    public decimal CachedInputFactor => CachedInputPerMillion is { } cached && InputPerMillion > 0 ? cached / InputPerMillion : 1m;
}

/// <summary>
/// The published prices of the models each provider serves, so budgets and spend are counted at
/// the right price without anyone typing it in (docs/llm-settings.md). A profile's own prices win;
/// a model that isn't listed here needs them. Prices are the standard tier at the provider's own
/// address, for prompts under any long-context threshold (see each model's note).
/// Update <see cref="AsOf"/> along with the prices.
/// </summary>
public static partial class ModelPriceCatalog
{
    /// <summary>When the prices were copied from the providers' price lists.</summary>
    public const string AsOf = "2026-10-08";

    /// <summary>Every listed model, newest first within each provider (the order the model picker shows).</summary>
    public static IReadOnlyList<ModelPrice> All { get; } =
    [
        // https://platform.claude.com/docs/en/about-claude/pricing
        new("Anthropic", "claude-fable-5-1", "Claude Fable 5.1", 10m, 50m, 0.25m),
        new("Anthropic", "claude-opus-5-5", "Claude Opus 5.5", 4m, 20m, 0.20m),
        new("Anthropic", "claude-sonnet-5-5", "Claude Sonnet 5.5", 2m, 10m, 0.10m),
        new("Anthropic", "claude-haiku-5-5", "Claude Haiku 5.5", 0.10m, 0.50m, 0.01m, "Prompts over 100k tokens: $0.50 in, $2.50 out."),
        new("Anthropic", "claude-fable-5", "Claude Fable 5", 10m, 50m, 1m),
        new("Anthropic", "claude-opus-5", "Claude Opus 5", 5m, 25m, 0.50m),
        new("Anthropic", "claude-sonnet-5", "Claude Sonnet 5", 2m, 10m, 0.20m),
        new("Anthropic", "claude-opus-4-8", "Claude Opus 4.8", 5m, 25m, 0.50m),
        new("Anthropic", "claude-opus-4-7", "Claude Opus 4.7", 5m, 25m, 0.50m),
        new("Anthropic", "claude-opus-4-6", "Claude Opus 4.6", 5m, 25m, 0.50m),
        new("Anthropic", "claude-sonnet-4-6", "Claude Sonnet 4.6", 3m, 15m, 0.30m),
        new("Anthropic", "claude-opus-4-5", "Claude Opus 4.5", 5m, 25m, 0.50m),
        new("Anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", 3m, 15m, 0.30m),
        new("Anthropic", "claude-haiku-4-5", "Claude Haiku 4.5", 1m, 5m, 0.10m),

        // https://developers.openai.com/api/docs/pricing
        new("OpenAI", "gpt-6-astra", "GPT-6 Astra", 10m, 50m, 1m, "Prompts over 272k tokens: $20 in, $75 out."),
        new("OpenAI", "gpt-6.1-sol", "GPT-6.1 Sol", 2m, 10m, 0.10m, "Prompts over 272k tokens: $4 in, $15 out."),
        new("OpenAI", "gpt-6-sol", "GPT-6 Sol", 2m, 10m, 0.20m, "Prompts over 272k tokens: $4 in, $15 out."),
        new("OpenAI", "gpt-6-luna", "GPT-6 Luna", 0.10m, 0.50m, 0.01m, "Prompts over 272k tokens: $0.20 in, $0.75 out."),
        new("OpenAI", "gpt-5.6-sol", "GPT-5.6 Sol", 4m, 20m, 0.40m, "Prompts over 272k tokens: $8 in, $30 out."),
        new("OpenAI", "gpt-5.6-terra", "GPT-5.6 Terra", 2m, 12m, 0.20m, "Prompts over 272k tokens: $4 in, $18 out."),
        new("OpenAI", "gpt-5.6-luna", "GPT-5.6 Luna", 0.20m, 1.20m, 0.02m, "Prompts over 272k tokens: $0.40 in, $1.80 out."),
        new("OpenAI", "gpt-5.5", "GPT-5.5", 5m, 30m, 0.50m, "Prompts over 272k tokens: $10 in, $45 out."),
        new("OpenAI", "gpt-5.5-pro", "GPT-5.5 Pro", 30m, 180m),
        new("OpenAI", "gpt-5.4", "GPT-5.4", 2.50m, 15m, 0.25m, "Prompts over 272k tokens: $5 in, $22.50 out."),
        new("OpenAI", "gpt-5.4-mini", "GPT-5.4 mini", 0.75m, 4.50m, 0.075m),
        new("OpenAI", "gpt-5.4-nano", "GPT-5.4 nano", 0.20m, 1.25m, 0.02m),
        new("OpenAI", "gpt-5.4-pro", "GPT-5.4 Pro", 30m, 180m, null, "Prompts over 272k tokens: $60 in, $270 out."),
        new("OpenAI", "gpt-5.3-codex", "GPT-5.3 Codex", 1.75m, 14m, 0.175m),
        new("OpenAI", "gpt-5.2", "GPT-5.2", 1.75m, 14m, 0.175m),
        new("OpenAI", "gpt-5.2-pro", "GPT-5.2 Pro", 21m, 168m),
        new("OpenAI", "gpt-5.1", "GPT-5.1", 1.25m, 10m, 0.125m),
        new("OpenAI", "gpt-5", "GPT-5", 1.25m, 10m, 0.125m),
        new("OpenAI", "gpt-5-mini", "GPT-5 mini", 0.25m, 2m, 0.025m),
        new("OpenAI", "gpt-5-nano", "GPT-5 nano", 0.05m, 0.40m, 0.005m),
        new("OpenAI", "gpt-5-pro", "GPT-5 Pro", 15m, 120m),
        new("OpenAI", "gpt-4.1", "GPT-4.1", 2m, 8m, 0.50m),
        new("OpenAI", "gpt-4.1-mini", "GPT-4.1 mini", 0.40m, 1.60m, 0.10m),
        new("OpenAI", "gpt-4.1-nano", "GPT-4.1 nano", 0.10m, 0.40m, 0.025m),
        new("OpenAI", "gpt-4o", "GPT-4o", 2.50m, 10m, 1.25m),
        new("OpenAI", "gpt-4o-2024-05-13", "GPT-4o (2024-05-13)", 5m, 15m),
        new("OpenAI", "gpt-4o-mini", "GPT-4o mini", 0.15m, 0.60m, 0.075m),
        new("OpenAI", "o3", "o3", 2m, 8m, 0.50m),
        new("OpenAI", "o3-pro", "o3 Pro", 20m, 80m),
        new("OpenAI", "o4-mini", "o4-mini", 1.10m, 4.40m, 0.275m),
        new("OpenAI", "o3-mini", "o3-mini", 1.10m, 4.40m, 0.55m),
        new("OpenAI", "o1", "o1", 15m, 60m, 7.50m),

        // https://ai.google.dev/gemini-api/docs/pricing
        new("Gemini", "gemini-3.8-flash", "Gemini 3.8 Flash", 0.75m, 3.75m, 0.075m, "Rises to $1.50 in, $7.50 out on January 1, 2027."),
        new("Gemini", "gemini-3.7-flash", "Gemini 3.7 Flash", 0.75m, 3.75m, 0.075m, "Rises to $1.50 in, $7.50 out on January 1, 2027."),
        new("Gemini", "gemini-3.6-flash", "Gemini 3.6 Flash", 0.75m, 3.75m, 0.075m, "Rises to $1.50 in, $7.50 out on January 1, 2027."),
        new("Gemini", "gemini-3.5-flash", "Gemini 3.5 Flash", 1.50m, 9m, 0.15m),
        new("Gemini", "gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", 0.30m, 2.50m, 0.03m),
        new("Gemini", "gemini-3.1-pro-preview", "Gemini 3.1 Pro (preview)", 2m, 12m, 0.20m, "Prompts over 200k tokens: $4 in, $18 out."),
        new("Gemini", "gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", 0.25m, 1.50m, 0.025m),
        new("Gemini", "gemini-3-flash-preview", "Gemini 3 Flash (preview)", 0.50m, 3m, 0.05m),
        new("Gemini", "gemini-2.5-pro", "Gemini 2.5 Pro", 1.25m, 10m, 0.125m, "Prompts over 200k tokens: $2.50 in, $15 out."),
        new("Gemini", "gemini-2.5-flash", "Gemini 2.5 Flash", 0.30m, 2.50m, 0.03m),
        new("Gemini", "gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", 0.10m, 0.40m, 0.01m),
    ];

    /// <summary>The listed models of one provider.</summary>
    public static IEnumerable<ModelPrice> For(string provider) =>
        All.Where(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A model's list price, or null when it isn't listed. Dated snapshots and "-latest" aliases
    /// ("gpt-4o-mini-2024-07-18", "claude-haiku-4-5-20251001") are priced as their model, and
    /// Gemini's "models/" prefix is ignored. Only exact matches count otherwise: an unknown
    /// "gpt-5-turbo" is not priced as "gpt-5".
    /// </summary>
    public static ModelPrice? Find(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return null;
        var id = model.Trim();
        if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];

        return Lookup(provider, id) ?? (SnapshotSuffix().Match(id) is { Success: true } m ? Lookup(provider, id[..m.Index]) : null);
    }

    private static ModelPrice? Lookup(string provider, string id) =>
        For(provider).FirstOrDefault(p => p.Model.Equals(id, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}|\d{8}|latest)$", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotSuffix();

    /// <summary>
    /// Fills in list prices the configuration left out: for the model, the fast model, and the
    /// cached-input rate. Prices set explicitly (by <paramref name="isConfigured"/>'s keys, e.g.
    /// "PricePerInputTokenUsd") always win. Only for the provider's own address: a custom base
    /// URL is another service with its own prices.
    /// </summary>
    public static void FillServerPrices(LlmOptions options, Func<string, bool> isConfigured)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseUrl)) return;

        if (Find(options.Provider, options.Model) is { } listed)
        {
            if (!isConfigured(nameof(LlmOptions.PricePerInputTokenUsd))) options.PricePerInputTokenUsd = listed.InputPerToken;
            if (!isConfigured(nameof(LlmOptions.PricePerOutputTokenUsd))) options.PricePerOutputTokenUsd = listed.OutputPerToken;
            if (!isConfigured(nameof(LlmOptions.CachedInputPriceFactor))) options.CachedInputPriceFactor = listed.CachedInputFactor;
        }

        if (Find(options.Provider, options.FastModel) is { } fast)
        {
            if (!isConfigured(nameof(LlmOptions.FastPricePerInputTokenUsd))) options.FastPricePerInputTokenUsd = fast.InputPerToken;
            if (!isConfigured(nameof(LlmOptions.FastPricePerOutputTokenUsd))) options.FastPricePerOutputTokenUsd = fast.OutputPerToken;
        }
    }
}
