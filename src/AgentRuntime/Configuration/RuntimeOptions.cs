using AgentRuntime.Contracts;

namespace AgentRuntime.Configuration;

/// <summary>Global spawn/depth ceilings, always enforced by the runtime (CLAUDE.md section 10).</summary>
public sealed class RuntimeLimitsOptions
{
    public const string SectionName = "RuntimeLimits";

    public int MaxAgentDepth { get; set; } = 5;
    public int MaxChildrenPerAgent { get; set; } = 10;
    public int MaxTotalAgents { get; set; } = 100;
    public int MaxActiveAgents { get; set; } = 50;

    /// <summary>Loop/cycle guards (CLAUDE.md section 23).</summary>
    public int MaxMessagesPerTask { get; set; } = 500;
    public int MaxReasoningIterationsPerTurn { get; set; } = 25;
    public int MaxRepeatedIdenticalToolCalls { get; set; } = 3;

    /// <summary>
    /// A spawn whose budget share falls below these is rejected, and the parent is told to do the
    /// work itself. Every LLM turn resends the system prompt, tool list, and whole transcript, so a
    /// real model spends a few thousand tokens per turn; a child funded below this can't finish
    /// anything and just fails "budget exhausted" after a turn or two.
    /// </summary>
    public int MinChildTokens { get; set; } = 20_000;
    public int MinChildToolCalls { get; set; } = 5;
}

public sealed class DefaultBudgetOptions
{
    public const string SectionName = "DefaultBudget";

    public int MaxTokens { get; set; } = 50_000;
    public int MaxDurationSeconds { get; set; } = 900;
    public int MaxChildren { get; set; } = 5;
    public int MaxToolCalls { get; set; } = 100;
    public decimal MaxCostUsd { get; set; } = 2.00m;

    public ResourceBudget ToBudget() => new()
    {
        MaxTokens = MaxTokens,
        MaxDurationSeconds = MaxDurationSeconds,
        MaxChildren = MaxChildren,
        MaxToolCalls = MaxToolCalls,
        MaxCostUsd = MaxCostUsd
    };
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "Mock";

    /// <summary>True when the model runs on local hardware (Ollama): free per token, but slow and
    /// usually serving one request at a time.</summary>
    public bool IsLocal => Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
    public string Model { get; set; } = "claude-sonnet-5";
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }

    /// <summary>USD per input token, used only to derive an approximate running cost for budgets.</summary>
    public decimal PricePerInputTokenUsd { get; set; } = 0.000003m;
    public decimal PricePerOutputTokenUsd { get; set; } = 0.000015m;

    /// <summary>A cheaper/faster model (same provider) for routine work: standing agents handling
    /// events, simulation residents, and summarizing long histories. Empty: everything uses Model.</summary>
    public string? FastModel { get; set; }
    public decimal? FastPricePerInputTokenUsd { get; set; }
    public decimal? FastPricePerOutputTokenUsd { get; set; }
    /// <summary>Output cap for routine (fast-tier) calls; planning calls keep MaxOutputTokens.</summary>
    public int FastMaxOutputTokens { get; set; } = 1024;
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>When an agent's history grows past roughly this many tokens, older steps are
    /// summarized (with the fast model) instead of being resent in full on every call.</summary>
    public int CompactAboveTokens { get; set; } = 40_000;
    /// <summary>Most recent transcript entries kept verbatim when compacting.</summary>
    public int CompactKeepRecentEntries { get; set; } = 24;

    public string ModelFor(bool fast) => fast && !string.IsNullOrWhiteSpace(FastModel) ? FastModel : Model;

    /// <summary>Price of a cached input token relative to a normal one. Defaults to the provider's
    /// published discount: Anthropic cache reads 0.1, OpenAI cached input 0.5, otherwise 1.</summary>
    public decimal? CachedInputPriceFactor { get; set; }

    /// <summary>Price of writing a token to the cache relative to a normal input token
    /// (Anthropic: 1.25); providers without explicit cache writes don't report any.</summary>
    public decimal? CacheWritePriceFactor { get; set; }

    /// <summary>Approximate cost of one completion, pricing cached and cache-written tokens at
    /// their own rates rather than as full-price input.</summary>
    public decimal CostOf(LLM.LlmCompletionResponse r, bool fast = false)
    {
        var usingFast = fast && !string.IsNullOrWhiteSpace(FastModel);
        var inPrice = usingFast ? FastPricePerInputTokenUsd ?? PricePerInputTokenUsd : PricePerInputTokenUsd;
        var outPrice = usingFast ? FastPricePerOutputTokenUsd ?? PricePerOutputTokenUsd : PricePerOutputTokenUsd;
        var cachedFactor = CachedInputPriceFactor ?? Provider switch { "Anthropic" => 0.1m, "OpenAI" => 0.5m, _ => 1m };
        var writeFactor = CacheWritePriceFactor ?? (Provider == "Anthropic" ? 1.25m : 1m);
        var uncached = Math.Max(0, r.InputTokens - r.CachedInputTokens - r.CacheWriteInputTokens);
        return uncached * inPrice
               + r.CachedInputTokens * inPrice * cachedFactor
               + r.CacheWriteInputTokens * inPrice * writeFactor
               + r.OutputTokens * outPrice;
    }

    /// <summary>Context window requested from providers that let the caller choose it (Ollama).</summary>
    public int ContextLength { get; set; } = 16384;

    /// <summary>Ask providers that support it (Ollama) not to generate a reasoning trace at all.
    /// The runtime never stores or shows chain-of-thought anyway; generating it only costs time.</summary>
    public bool DisableThinking { get; set; } = true;

    /// <summary>HTTP timeout for LLM calls. Local models can take minutes on a slow machine.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class AutonomyOptions
{
    public const string SectionName = "Autonomy";

    public AutonomyLevel Level { get; set; } = AutonomyLevel.Autonomous;
}

public sealed class SupervisionOptions
{
    public const string SectionName = "Supervision";

    public int MaxRetries { get; set; } = 3;
    public SupervisionAction FailurePolicy { get; set; } = SupervisionAction.Restart;
}
