using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Contracts;

namespace AgentRuntime.Evals;

/// <summary>What to evaluate: goals (scenarios) run N times under each variant (model, prompt or config).</summary>
public sealed record EvalSpec
{
    public List<EvalScenario> Scenarios { get; init; } = [];
    public List<EvalVariant> Variants { get; init; } = [new() { Name = "default" }];
    /// <summary>Longest one run may take before it counts as not completed.</summary>
    public int RunTimeoutSeconds { get; init; } = 300;
    /// <summary>"auto" (an LLM judge unless the provider is Mock), "heuristic" or "llm".</summary>
    public string Judge { get; init; } = "auto";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static EvalSpec Load(string path) =>
        JsonSerializer.Deserialize<EvalSpec>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException($"Empty eval spec: {path}");
}

public sealed record EvalScenario
{
    public required string Name { get; init; }
    public required string Goal { get; init; }
    public int Runs { get; init; } = 3;
    public ResourceBudget? Budget { get; init; }
    /// <summary>Words a good answer should mention (for the heuristic judge, and shown to the LLM judge).</summary>
    public List<string> ExpectedTopics { get; init; } = [];
}

/// <summary>One configuration to compare: settings override the runtime's configuration (e.g.
/// "Llm:Model", "Prompts:ExtraInstructions"). Mode "replay" runs once live and replays the rest from
/// that run's step journal, measuring how faithfully the runtime reproduces a run.</summary>
public sealed record EvalVariant
{
    public required string Name { get; init; }
    public Dictionary<string, string?> Settings { get; init; } = [];
    public string Mode { get; init; } = "live";
}

public sealed record RunResult
{
    public required string TaskId { get; init; }
    public bool Completed { get; init; }
    /// <summary>"completed", "partial", "failed", "timeout"…</summary>
    public required string Outcome { get; init; }
    public int TeamSize { get; init; }
    public int MaxDepth { get; init; }
    public long Tokens { get; init; }
    public decimal CostUsd { get; init; }
    public double DurationSeconds { get; init; }
    public double Quality { get; init; }
    public string? QualityRationale { get; init; }
    public string? Summary { get; init; }
    /// <summary>Replay variants: whether the run's decisions matched the recorded run step for step.</summary>
    public bool? Reproduced { get; init; }
    public string? Error { get; init; }
}

public sealed record Stat(double Mean, double Min, double Max, double P50, double P95)
{
    public static Stat Of(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return new Stat(0, 0, 0, 0, 0);
        double Pct(double p) => v[(int)Math.Min(v.Count - 1, Math.Ceiling(p * v.Count) - 1)];
        return new Stat(Math.Round(v.Average(), 4), v[0], v[^1], Pct(0.5), Pct(0.95));
    }
}

/// <summary>One scenario under one variant.</summary>
public sealed record EvalCell
{
    public required string Scenario { get; init; }
    public required string Variant { get; init; }
    public required string Model { get; init; }
    public required string PromptVersion { get; init; }
    public int Runs { get; init; }
    public double CompletionRate { get; init; }
    public required Stat TeamSize { get; init; }
    public required Stat Depth { get; init; }
    public required Stat Tokens { get; init; }
    public required Stat CostUsd { get; init; }
    public required Stat DurationSeconds { get; init; }
    public required Stat Quality { get; init; }
    public double? ReplayFidelity { get; init; }
    public List<RunResult> Results { get; init; } = [];
}

public sealed record EvalReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Judge { get; init; } = "heuristic";
    public List<EvalCell> Cells { get; init; } = [];
}

public sealed record Regression(string Scenario, string Variant, string Metric, double Baseline, double Current, string Message);
