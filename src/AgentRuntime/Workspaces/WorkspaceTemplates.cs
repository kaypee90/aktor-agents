using AgentRuntime.Contracts;
using AgentRuntime.Safety;

namespace AgentRuntime.Workspaces;

/// <summary>A connection a template sets up (e.g. the simulated production system).</summary>
public sealed record TemplateConnection(string PluginId, string Name, bool DemoOnly);

/// <summary>A webhook a template sets up, and an example payload to try it with.</summary>
public sealed record TemplateWebhook(string Name, string Instruction, string SamplePayload);

/// <summary>
/// A ready-made workspace: its standing goal (the coordinator's instructions), safety policy,
/// connections and triggers. The goal is plain instructions, not a workflow: the coordinator still
/// decides what to do with each event, and the runtime still enforces every limit.
/// </summary>
public sealed record WorkspaceTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Goal { get; init; }
    public required WorkspaceSafetyPolicy Safety { get; init; }
    public List<TemplateConnection> Connections { get; init; } = [];
    public List<TemplateWebhook> Webhooks { get; init; } = [];
    public int? DailyTokenLimit { get; init; }
    public decimal? DailyCostLimitUsd { get; init; }
}

public static class WorkspaceTemplates
{
    public const string IncidentResponseId = "incident-response";

    /// <summary>Recognisable in the coordinator's goal (the Mock provider scripts the demo on it).</summary>
    public const string IncidentResponseMarker = "[incident-response template]";

    /// <summary>
    /// Ops and incident investigation (roadmap P9): an alert wakes the coordinator; it starts
    /// investigators for logs, metrics and recent deploys in parallel, writes an incident report,
    /// and proposes a fix that waits for a human's approval (SemiAutonomous, plus an explicit rule
    /// for rollbacks). Everything is audited.
    /// </summary>
    public static readonly WorkspaceTemplate IncidentResponse = new()
    {
        Id = IncidentResponseId,
        Name = "Incident response",
        Description = "Alerts wake an investigation team (logs, metrics, recent deploys), which writes an incident report " +
                      "and proposes a fix that waits for your approval.",
        Goal = $"""
            {IncidentResponseMarker} Investigate production incidents and propose fixes for the user's services.
            When an alert arrives through the "Incoming alerts" webhook:
            1. Call plan_request with three independent, medium parts: the logs, the metrics, and recent deploys of the affected service.
            2. Start one investigator per part (spawn_agent, standing=false). Give each a goal naming the service, the time
               window, and the connection tool to use: ops__query_logs, ops__query_metrics or ops__list_deploys. Then call wait_for_events.
            3. When all three have reported, write incident-report.md (filesystem_write) with: summary, impact, timeline,
               evidence from each investigator, likely cause, proposed fix, and follow-ups.
            4. If a recent deploy is the likely cause, propose a rollback with ops__rollback_deploy. It needs the user's
               approval: say in your message why you propose it. Never roll back without evidence.
            5. Tell the user (notify_user, urgency "urgent") what happened, what you propose, and where the report is.
            If an alert turns out to be noise, say so and stop. Until an alert arrives, there is nothing to do.
            """,
        Safety = new WorkspaceSafetyPolicy
        {
            Autonomy = AutonomyLevel.SemiAutonomous,
            ApprovalTimeoutHours = 4,
            Rules =
            [
                new ApprovalRule { Name = "Rollbacks need approval", ToolPattern = "*__rollback*", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.RequireApproval },
                new ApprovalRule { Name = "No shell in incidents", ToolPattern = "shell_exec", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }
            ],
            // Investigations stay small: the coordinator and its investigators, never a crowd.
            Team = new TeamPolicy { MaxAgents = 6, MaxFanOutByDepth = [3, 0], CountFinishedAgents = false }
        },
        Connections = [new TemplateConnection("demo-ops", "ops", DemoOnly: true)],
        Webhooks =
        [
            new TemplateWebhook(
                "Incoming alerts",
                "An alert arrived. Investigate it as your goal describes: plan, start the investigators, then report.",
                """{"alert":"HighErrorRate","service":"checkout-service","severity":"critical","value":"18.4%","threshold":"2%","source":"monitoring"}""")
        ],
        DailyTokenLimit = 2_000_000,
        DailyCostLimitUsd = 10m
    };

    public static IReadOnlyList<WorkspaceTemplate> All { get; } = [IncidentResponse];

    public static WorkspaceTemplate? Get(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
