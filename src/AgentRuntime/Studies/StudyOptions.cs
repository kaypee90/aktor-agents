namespace AgentRuntime.Studies;

/// <summary>Section "Studies" (docs/studies.md). Limits the runtime enforces whatever agents ask for.</summary>
public sealed class StudyOptions
{
    public const string SectionName = "Studies";

    /// <summary>Where studies' datasets and charts are kept.</summary>
    public string DataRoot { get; set; } = "./workspace/studies";

    /// <summary>The analysis sandbox image (docker/analysis).</summary>
    public string AnalysisImage { get; set; } = "aktor-analysis:1";
    public string AnalysisMemoryLimit { get; set; } = "2g";
    public string AnalysisCpuLimit { get; set; } = "1";
    public int AnalysisTimeoutSeconds { get; set; } = 120;

    /// <summary>Largest dataset file accepted.</summary>
    public long MaxDatasetBytes { get; set; } = 50L * 1024 * 1024;
    public int MaxDatasetsPerStudy { get; set; } = 20;
    public double DefaultHoldoutFraction { get; set; } = 0.2;
    public int MaxQueryRows { get; set; } = 200;
    /// <summary>Holdout evaluations per study: few enough that the holdout can't be fished.</summary>
    public int MaxHoldoutEvaluations { get; set; } = 3;

    // Simulations (experiments).
    public int MaxParticipants { get; set; } = 40;
    public int MaxRounds { get; set; } = 4;
    public int MaxConditions { get; set; } = 3;
    public int MaxReplications { get; set; } = 3;
    /// <summary>Participants' decisions made at once.</summary>
    public int SimulationConcurrency { get; set; } = 4;
    /// <summary>A simulation stops (and reports what it has) beyond this cost.</summary>
    public decimal MaxSimulationCostUsd { get; set; } = 3m;

    /// <summary>A study's workspace: its daily token and dollar limits.</summary>
    public int DailyTokenLimit { get; set; } = 5_000_000;
    public decimal DailyCostLimitUsd { get; set; } = 25m;

    /// <summary>Budget of each study run (its root agent and every agent it starts).</summary>
    public int RunMaxTokens { get; set; } = 2_000_000;
    public int RunMaxToolCalls { get; set; } = 400;
    public int RunMaxDurationSeconds { get; set; } = 3600;
    public decimal RunMaxCostUsd { get; set; } = 10m;
    public int RunMaxChildren { get; set; } = 6;
}
