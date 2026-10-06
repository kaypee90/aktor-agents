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

    /// <summary>
    /// Share of any lifetime budget (tokens, tool calls, cost, time) after which an agent is told
    /// to start finishing. Near the end it gets one last call to report, and if even that doesn't
    /// fit the runtime reports for it: the parent always gets a result, never a bare failure.
    /// </summary>
    public double WrapUpAtFraction { get; set; } = 0.75;

    /// <summary>Output cap for that last call; it also sizes the budget kept back for it.</summary>
    public int FinalStepMaxOutputTokens { get; set; } = 1024;
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

/// <summary>
/// The most any single task may be granted, whatever its caller asks for. Applied by the runtime
/// when the root agent is created, so REST, MCP, A2A and ACP callers all get the same ceiling.
/// </summary>
public sealed class TaskBudgetCeilingOptions
{
    public const string SectionName = "TaskBudgetCeiling";

    public int MaxTokens { get; set; } = 5_000_000;
    public int MaxDurationSeconds { get; set; } = 4 * 60 * 60;
    public int MaxChildren { get; set; } = 20;
    public int MaxToolCalls { get; set; } = 5_000;
    public decimal MaxCostUsd { get; set; } = 50m;

    /// <summary>The requested budget with every limit capped at the ceiling.</summary>
    public ResourceBudget Clamp(ResourceBudget requested) => requested with
    {
        MaxTokens = Math.Clamp(requested.MaxTokens, 0, MaxTokens),
        MaxDurationSeconds = Math.Clamp(requested.MaxDurationSeconds, 0, MaxDurationSeconds),
        MaxChildren = Math.Clamp(requested.MaxChildren, 0, MaxChildren),
        MaxToolCalls = Math.Clamp(requested.MaxToolCalls, 0, MaxToolCalls),
        MaxCostUsd = Math.Clamp(requested.MaxCostUsd, 0, MaxCostUsd)
    };
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "Mock";

    /// <summary>Organizations may pick their own provider, model and key in the dashboard
    /// (docs/llm-settings.md). Off: every organization uses this server configuration.</summary>
    public bool AllowOrganizationSettings { get; set; } = true;

    /// <summary>Lets an organization point its provider at a private address (Ollama on this
    /// machine or the local network). Turn off on a shared server: base URLs are chosen by
    /// organization admins, and private ones reach your internal network.</summary>
    public bool AllowPrivateBaseUrls { get; set; } = true;

    /// <summary>The organization model profile these options came from; null for the server's own
    /// configuration (docs/llm-settings.md). Recorded with every model call for analytics.</summary>
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>A copy to adjust for one organization without touching the server's options.</summary>
    public LlmOptions Clone() => (LlmOptions)MemberwiseClone();

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

    /// <summary>Floor on the output cap sent to reasoning-era models (OpenAI's o-series, GPT-5
    /// and later), which draw reasoning tokens from the same allowance as the answer: a cap sized
    /// for a non-reasoning model (1024 here) can be spent before a single visible token is
    /// written, and the call comes back empty. OpenAI suggests reserving at least 25,000 tokens
    /// when starting with such a model. A cap is a ceiling, not a reservation — the call still
    /// costs only what it uses, and per-agent budgets still bound the total — so this only ever
    /// prevents a wasted call. 0 disables the floor.</summary>
    public int ReasoningMinOutputTokens { get; set; } = 25_000;

    /// <summary>When an agent's history grows past roughly this many tokens, older steps are
    /// summarized (with the fast model) instead of being resent in full on every call.</summary>
    public int CompactAboveTokens { get; set; } = 40_000;
    /// <summary>Most recent transcript entries kept verbatim when compacting.</summary>
    public int CompactKeepRecentEntries { get; set; } = 24;

    public string ModelFor(bool fast) => fast && !string.IsNullOrWhiteSpace(FastModel) ? FastModel : Model;

    /// <summary>Price of a cached input token relative to a normal one. Defaults to the provider's
    /// published discount: Anthropic cache reads 0.1, OpenAI cached input 0.5, Gemini cached
    /// content 0.25, otherwise 1.</summary>
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
        var cachedFactor = CachedInputPriceFactor ?? Provider switch { "Anthropic" => 0.1m, "OpenAI" => 0.5m, "Gemini" => 0.25m, _ => 1m };
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
