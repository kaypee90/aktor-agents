using AgentRuntime.Contracts;
using AgentRuntime.Safety;

namespace AgentRuntime.Workspaces;

/// <summary>A connection a template sets up (e.g. the simulated production system).</summary>
public sealed record TemplateConnection(string PluginId, string Name, bool DemoOnly);

/// <summary>A webhook a template sets up, and an example payload to try it with.</summary>
public sealed record TemplateWebhook(string Name, string Instruction, string SamplePayload);

/// <summary>A schedule a template sets up: a 5-field UTC cron and the run's input each time.</summary>
public sealed record TemplateSchedule(string Name, string Instruction, string Cron);

/// <summary>
/// A ready-made workspace: its purpose, its pipeline, safety policy, connections and triggers.
/// The pipeline is a starting point the user can change like any other; inside each stage the
/// agent still decides how to do its part, and the runtime still enforces every limit.
/// </summary>
public sealed record WorkspaceTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>For grouping in the gallery: Operations, Support, Research, Engineering, Sales, ...</summary>
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required string Goal { get; init; }
    /// <summary>An input to try the pipeline with straight away (a webhook template's is its sample payload).</summary>
    public string? SampleInput { get; init; }
    public required Pipelines.PipelineDefinition Pipeline { get; init; }
    public required WorkspaceSafetyPolicy Safety { get; init; }
    public List<TemplateConnection> Connections { get; init; } = [];
    public List<TemplateWebhook> Webhooks { get; init; } = [];
    public List<TemplateSchedule> Schedules { get; init; } = [];
    public int? DailyTokenLimit { get; init; }
    public decimal? DailyCostLimitUsd { get; init; }
}

public static partial class WorkspaceTemplates
{
    public const string IncidentResponseId = "incident-response";

    private static Pipelines.PipelineStage Investigator(string id, string name, string tool, string focus) => new()
    {
        StageId = id,
        Name = name,
        Role = name,
        Instructions = $"Using {tool}, investigate the {focus} of the service Triage named, over its time window. " +
                       "Report what you find with evidence (numbers, timestamps, messages), and what it suggests about the cause.",
        Inputs = ["triage"],
        Capabilities = ["research"],
        Retries = 1,
        OnFailure = Pipelines.StageFailurePolicy.Continue
    };

    /// <summary>
    /// Ops and incident investigation (roadmap P9): an alert starts a run; Triage reads it, three
    /// investigators (logs, metrics, recent deploys) work in parallel, an analyst writes the
    /// incident report, and a remediator proposes a fix that waits for a person's approval
    /// (SemiAutonomous, plus an explicit rule for rollbacks). Everything is audited.
    /// </summary>
    public static readonly WorkspaceTemplate IncidentResponse = new()
    {
        Id = IncidentResponseId,
        Name = "Incident response",
        Category = "Operations",
        Description = "Each alert runs a pipeline: triage, parallel investigation of logs, metrics and recent deploys, " +
                      "an incident report, and a proposed fix that waits for your approval.",
        Goal = "Investigate production incidents for the user's services and propose fixes, from each alert.",
        Pipeline = new Pipelines.PipelineDefinition
        {
            // Long enough for a person to approve the rollback (the approval timeout is 4 hours).
            MaxRunMinutes = 240,
            MaxConcurrentRuns = 2,
            // An incident's outcome reaches the on-call person's channels (SMS, Slack) at once.
            ResultUrgency = "urgent",
            Stages =
            [
                new Pipelines.PipelineStage
                {
                    StageId = "triage",
                    Name = "Triage",
                    Role = "Incident triager",
                    Instructions = "Read the alert in the run's input. State the affected service, the symptom, the severity, and the " +
                                   "time window to investigate (default: the last 60 minutes). If the alert is noise (a test, a duplicate, " +
                                   "below its threshold), say so plainly so the next stages can stop early.",
                    Capabilities = ["research"]
                },
                Investigator("logs", "Logs investigator", "ops__query_logs", "error logs"),
                Investigator("metrics", "Metrics investigator", "ops__query_metrics", "error rate and latency"),
                Investigator("deploys", "Deploy investigator", "ops__list_deploys", "recent deploys"),
                new Pipelines.PipelineStage
                {
                    StageId = "diagnose",
                    Name = "Diagnose",
                    Role = "Incident analyst",
                    Instructions = "Combine the investigators' findings into incident-report.md (filesystem_write) with: summary, impact, " +
                                   "timeline, evidence from each investigator, likely cause, proposed fix, and follow-ups. Say clearly " +
                                   "whether a recent deploy is the likely cause and which version to roll back to.",
                    Inputs = ["logs", "metrics", "deploys"],
                    Capabilities = ["research", "filesystem"]
                },
                new Pipelines.PipelineStage
                {
                    StageId = "remediate",
                    Name = "Remediate",
                    Role = "Remediator",
                    Instructions = "If the analysis shows a recent deploy is the likely cause, propose a rollback with ops__rollback_deploy " +
                                   "(it waits for a person's approval) and say why. Never roll back without evidence. Report what was " +
                                   "done, what is still needed, and point to incident-report.md.",
                    Inputs = ["diagnose"],
                    Capabilities = ["research"],
                    // A rollback isn't something to try twice on its own.
                    Retries = 0
                }
            ]
        },
        Safety = new WorkspaceSafetyPolicy
        {
            Autonomy = AutonomyLevel.SemiAutonomous,
            ApprovalTimeoutHours = 4,
            Rules =
            [
                new ApprovalRule { Name = "Rollbacks need approval", ToolPattern = "*__rollback*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.RequireApproval },
                new ApprovalRule { Name = "No shell in incidents", ToolPattern = "shell_exec", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }
            ]
        },
        Connections = [new TemplateConnection("demo-ops", "ops", DemoOnly: true)],
        Webhooks =
        [
            new TemplateWebhook(
                "Incoming alerts",
                "Investigate this alert.",
                """{"alert":"HighErrorRate","service":"checkout-service","severity":"critical","value":"18.4%","threshold":"2%","source":"monitoring"}""")
        ],
        DailyTokenLimit = 2_000_000,
        DailyCostLimitUsd = 10m
    };

    // A property, not a field: the templates are spread over partial files, whose static
    // initializers run in no guaranteed order.
    public static IReadOnlyList<WorkspaceTemplate> All =>
    [
        IncidentResponse, SupportTriage, MarketResearch, PullRequestReview, LeadResearch,
        ContentProduction, CompetitiveIntelligence, ContractReview, CandidateScreening
    ];

    public static WorkspaceTemplate? Get(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
