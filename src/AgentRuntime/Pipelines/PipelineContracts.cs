namespace AgentRuntime.Pipelines;

/// <summary>Ids of pipeline runs. A run is the root of its agent tree (its stage agents' parent),
/// so its id lives in the same namespace as agent ids and is told apart by its prefix.</summary>
public static class PipelineIds
{
    public const string RunPrefix = "run-";

    public static bool IsRun(string? id) => id?.StartsWith(RunPrefix, StringComparison.Ordinal) == true;

    public static string NewRun() => RunPrefix + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The same run id for the same event (a trigger tick or a webhook delivery), so a
    /// repeated fire after a crash finds the run it already started.</summary>
    public static string RunFor(string eventId) => Contracts.DeterministicId.From(RunPrefix, eventId);
}

/// <summary>What a stage does when its agent fails after its retries.</summary>
public enum StageFailurePolicy
{
    /// <summary>The run fails; stages not started yet are skipped.</summary>
    FailRun,
    /// <summary>Stages that need it still run, told it failed and why.</summary>
    Continue
}

/// <summary>
/// One step of a workspace's pipeline: an agent with a role, instructions and tools, that runs
/// once its inputs have finished and hands its result to the stages that take it as input.
/// Inside its step the agent is autonomous (tools, helpers, messages to other stages) within
/// the limits set here and by the workspace's policy.
/// </summary>
[GenerateSerializer]
public sealed record PipelineStage
{
    /// <summary>Short, stable id (lowercase letters, digits and dashes), unique in the pipeline.</summary>
    [Id(0)] public required string StageId { get; init; }
    [Id(1)] public required string Name { get; init; }
    /// <summary>The agent's role, e.g. "Market researcher". Defaults to the name.</summary>
    [Id(2)] public string Role { get; init; } = string.Empty;
    /// <summary>What the stage's agent does with the run's input and its inputs' results.</summary>
    [Id(3)] public string Instructions { get; init; } = string.Empty;
    /// <summary>Stages whose results this one needs; it starts when all of them have finished.
    /// Empty: an entry stage, started with the run.</summary>
    [Id(4)] public List<string> Inputs { get; init; } = [];
    /// <summary>Capability words that grant tools (see AgentToolCatalog), e.g. "research",
    /// "filesystem", "http". Empty: research and files.</summary>
    [Id(5)] public List<string> Capabilities { get; init; } = [];
    /// <summary>A model profile for this stage; null uses the workspace's model.</summary>
    [Id(6)] public string? ModelProfileId { get; init; }
    /// <summary>Helpers the stage's agent may start (0: it works alone).</summary>
    [Id(7)] public int MaxHelpers { get; init; }
    /// <summary>Whether the agent may message the run's other stages directly.</summary>
    [Id(8)] public bool MayMessageStages { get; init; } = true;
    /// <summary>Extra attempts after a failure before <see cref="OnFailure"/> applies.</summary>
    [Id(9)] public int Retries { get; init; } = 1;
    [Id(10)] public StageFailurePolicy OnFailure { get; init; } = StageFailurePolicy.FailRun;
    /// <summary>Most this stage may spend per run, in US dollars; null: the workspace's per-agent budget.</summary>
    [Id(11)] public decimal? MaxCostUsd { get; init; }

    public string EffectiveRole => string.IsNullOrWhiteSpace(Role) ? Name : Role;
}

/// <summary>Where a stage sits on the canvas (presentation only; runs don't depend on it).</summary>
[GenerateSerializer]
public sealed record StagePosition([property: Id(0)] double X, [property: Id(1)] double Y);

/// <summary>A workspace's pipeline at one version. Every applied change makes a new version.</summary>
[GenerateSerializer]
public sealed record PipelineDefinition
{
    [Id(0)] public int Version { get; init; }
    [Id(1)] public List<PipelineStage> Stages { get; init; } = [];
    [Id(2)] public DateTimeOffset UpdatedAt { get; init; }
    [Id(3)] public string UpdatedBy { get; init; } = string.Empty;
    /// <summary>What changed in this version, e.g. "Added Security reviewer after Backend".</summary>
    [Id(4)] public string Note { get; init; } = string.Empty;
    /// <summary>Most minutes one run may take, from start to its last stage.</summary>
    [Id(5)] public int MaxRunMinutes { get; init; } = 30;
    /// <summary>Runs that may be in progress at once; more wait in a queue.</summary>
    [Id(6)] public int MaxConcurrentRuns { get; init; } = 2;
    /// <summary>How a finished run's result is posted: "info", "warning" or "urgent". Connected
    /// channels (SMS, Slack, email) forward it by their notify level. Failures are at least warnings.</summary>
    [Id(7)] public string ResultUrgency { get; init; } = "info";
    /// <summary>Stages a person placed on the canvas, by stage id; the others are laid out
    /// automatically. Moving stages changes this in place, without a new version.</summary>
    [Id(8)] public Dictionary<string, StagePosition> Layout { get; init; } = [];

    public PipelineStage? Find(string stageId) =>
        Stages.FirstOrDefault(s => string.Equals(s.StageId, stageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stages that take <paramref name="stageId"/> as an input.</summary>
    public IEnumerable<PipelineStage> DependentsOf(string stageId) =>
        Stages.Where(s => s.Inputs.Contains(stageId, StringComparer.OrdinalIgnoreCase));

    /// <summary>The stages nothing depends on: their results are the run's result.</summary>
    public IEnumerable<PipelineStage> Outputs() => Stages.Where(s => !DependentsOf(s.StageId).Any());
}

/// <summary>Kinds of change to a pipeline; see <see cref="PipelineEditor"/>.</summary>
public static class PipelineEditOps
{
    public const string AddStage = "add_stage";
    public const string UpdateStage = "update_stage";
    public const string RemoveStage = "remove_stage";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";

    public static readonly string[] All = [AddStage, UpdateStage, RemoveStage, Connect, Disconnect];
}

/// <summary>
/// One change to a pipeline, as the natural-language editor (or the canvas) proposes it.
/// <list type="bullet">
/// <item><c>add_stage</c>: <see cref="Stage"/>, placed by <see cref="After"/> and/or <see cref="Before"/>
/// (inserted on that connection), or by the stage's own inputs.</item>
/// <item><c>update_stage</c>: <see cref="StageId"/> and the fields of <see cref="Stage"/> to change.</item>
/// <item><c>remove_stage</c>: <see cref="StageId"/>; its inputs are connected to its dependents, so
/// the pipeline stays joined up.</item>
/// <item><c>connect</c> / <c>disconnect</c>: <see cref="From"/> → <see cref="To"/>.</item>
/// <item>With <see cref="Position"/>, an added stage is placed there on the canvas (the canvas only).</item>
/// </list>
/// </summary>
[GenerateSerializer]
public sealed record PipelineEditOp
{
    [Id(0)] public required string Op { get; init; }
    [Id(1)] public string? StageId { get; init; }
    [Id(2)] public PipelineStagePatch? Stage { get; init; }
    [Id(3)] public string? After { get; init; }
    [Id(4)] public string? Before { get; init; }
    [Id(5)] public string? From { get; init; }
    [Id(6)] public string? To { get; init; }
    [Id(7)] public StagePosition? Position { get; init; }
}

/// <summary>Stage fields for an edit; null leaves a field as it is (or at its default, when adding).</summary>
[GenerateSerializer]
public sealed record PipelineStagePatch
{
    [Id(0)] public string? StageId { get; init; }
    [Id(1)] public string? Name { get; init; }
    [Id(2)] public string? Role { get; init; }
    [Id(3)] public string? Instructions { get; init; }
    [Id(4)] public List<string>? Inputs { get; init; }
    [Id(5)] public List<string>? Capabilities { get; init; }
    [Id(6)] public string? ModelProfileId { get; init; }
    [Id(7)] public int? MaxHelpers { get; init; }
    [Id(8)] public bool? MayMessageStages { get; init; }
    [Id(9)] public int? Retries { get; init; }
    [Id(10)] public StageFailurePolicy? OnFailure { get; init; }
    [Id(11)] public decimal? MaxCostUsd { get; init; }
}

/// <summary>Limits a pipeline must stay within (configuration, never the LLM).</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipelines";

    public int MaxStages { get; set; } = 20;
    public int MaxHelpersPerStage { get; set; } = 5;
    public int MaxRetries { get; set; } = 3;
    public int MaxRunMinutes { get; set; } = 240;
    public int MaxConcurrentRuns { get; set; } = 5;
    /// <summary>Runs waiting for a free slot; triggers beyond this are dropped and counted.</summary>
    public int MaxQueuedRuns { get; set; } = 20;
    public int MaxInstructionsLength { get; set; } = 4000;
    /// <summary>Most characters of the run's input and of each upstream result a stage is given.</summary>
    public int MaxContextChars { get; set; } = 12_000;
    /// <summary>Versions kept for undo.</summary>
    public int MaxVersionsKept { get; set; } = 30;
}
