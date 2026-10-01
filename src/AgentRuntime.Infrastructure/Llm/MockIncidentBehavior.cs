using System.Text;
using System.Text.Json;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's script for the incident-response template (roadmap P9), so the flagship demo
/// runs end to end with no API key: an alert → plan → three investigators (logs, metrics, deploys)
/// each query the ops connection and report → the coordinator writes incident-report.md → proposes
/// a rollback (which waits for approval) → tells the user. It reads only the transcript, like every
/// Mock behavior; a real model follows the same instructions from the workspace goal.
/// </summary>
internal static class MockIncidentBehavior
{
    private const string Alert = "[Webhook 'Incoming alerts'";

    private static readonly (string Role, string Tool, string Focus)[] Investigators =
    [
        ("Logs Investigator", "ops__query_logs", "error logs"),
        ("Metrics Investigator", "ops__query_metrics", "error rate and latency"),
        ("Deploy Investigator", "ops__list_deploys", "recent deploys")
    ];

    public static LlmCompletionResponse? TryRespond(LlmCompletionRequest request)
    {
        var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
        var tools = request.Tools.Select(t => t.Name).ToHashSet();

        var investigator = Investigators.FirstOrDefault(i => system.Contains($"acting as: {i.Role}.", StringComparison.Ordinal));
        if (investigator.Role is not null && tools.Contains(investigator.Tool)) return Investigate(request, investigator);

        if (system.Contains("acting as: Coordinator.", StringComparison.Ordinal) &&
            system.Contains(AgentRuntime.Workspaces.WorkspaceTemplates.IncidentResponseMarker, StringComparison.Ordinal))
        {
            return Coordinate(request, tools);
        }

        return null;
    }

    private static LlmCompletionResponse Coordinate(LlmCompletionRequest request, HashSet<string> tools)
    {
        var messages = request.Messages.ToList();
        var alertIndex = messages.FindLastIndex(m => m.Role == ChatRole.User && m.Content?.Contains(Alert, StringComparison.Ordinal) == true);
        var lastUser = messages.FindLastIndex(m => m.Role == ChatRole.User);

        // Before any alert (the workspace's first request), or after the incident is handled: stand by.
        if (alertIndex < 0)
        {
            var actedSinceInput = messages.Skip(lastUser + 1).Any(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 });
            return actedSinceInput
                ? Respond(Call("wait_for_events", new { summary = "Waiting for alerts." }))
                : Respond(Call("notify_user", new
                {
                    text = "Incident response is set up. When an alert arrives I'll start investigators for logs, metrics and recent deploys, " +
                           "write an incident report, and propose a fix for your approval. (Mock LLM: connect a real model for real investigations.)"
                }));
        }

        var since = messages.Skip(alertIndex + 1).ToList();
        var called = since.Where(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 }).SelectMany(m => m.ToolCalls!).Select(c => c.Name).ToList();
        var reports = since.Where(m => m.Role == ChatRole.User && m.Content?.Contains("Your child agent", StringComparison.Ordinal) == true)
            .Select(m => m.Content!).ToList();

        if (!called.Contains("plan_request"))
        {
            return Respond(Call("plan_request", new
            {
                parts = Investigators.Select(i => new { title = $"Investigate {i.Focus} for checkout-service", size = "medium", depends_on_other_parts = false })
            }), "An alert came in for checkout-service. Planning the investigation.");
        }

        if (!called.Contains("spawn_agent"))
        {
            return Respond(Investigators.Select(i => Call("spawn_agent", new
            {
                role = i.Role,
                goal = $"Investigate the {i.Focus} of checkout-service over the last 60 minutes using {i.Tool}, and report what you find with evidence.",
                capabilities = new[] { "research" },
                why_not_myself = "The three lines of investigation run in parallel; an incident needs answers fast."
            })).ToArray());
        }

        if (reports.Count < Investigators.Length)
        {
            return Respond(Call("wait_for_events", new { summary = $"Waiting for investigators ({reports.Count}/{Investigators.Length} reported)." }));
        }

        if (!called.Contains("filesystem_write") && tools.Contains("filesystem_write"))
        {
            return Respond(Call("filesystem_write", new { path = "incident-report.md", content = Report(reports) }));
        }

        if (!called.Contains("ops__rollback_deploy") && tools.Contains("ops__rollback_deploy"))
        {
            return Respond(Call("ops__rollback_deploy", new
            {
                service = "checkout-service",
                to_version = "v2.13.2",
                reason = "Errors began one minute after deploy #4812 (v2.14.0): NullReferenceException in DiscountService.ApplyLoyaltyDiscount."
            }), "The evidence points at deploy #4812 (v2.14.0). I propose rolling checkout-service back to v2.13.2; this needs your approval.");
        }

        if (!called.Contains("notify_user"))
        {
            var rollback = since.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == "ops__rollback_deploy")?.Content ?? string.Empty;
            var rolledBack = rollback.Contains("\"rolled_back\":true", StringComparison.Ordinal);
            return Respond(Call("notify_user", new
            {
                urgency = "urgent",
                text = "Incident: checkout-service error rate rose to 18.4% one minute after deploy #4812 (v2.14.0), from a " +
                       "NullReferenceException in DiscountService.ApplyLoyaltyDiscount. " +
                       (rolledBack ? "With your approval I rolled it back to v2.13.2. " : "The rollback to v2.13.2 did not run: " + Clip(rollback, 160) + " ") +
                       "Full write-up: incident-report.md (Files tab)."
            }));
        }

        return Respond(Call("wait_for_events", new { summary = "Incident handled; waiting for the next alert." }));
    }

    private static LlmCompletionResponse Investigate(LlmCompletionRequest request, (string Role, string Tool, string Focus) me)
    {
        var result = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == me.Tool)?.Content;
        if (result is null)
        {
            object args = me.Tool switch
            {
                "ops__query_logs" => new { service = "checkout-service", level = "error", since_minutes = 60 },
                "ops__query_metrics" => new { service = "checkout-service", metric = "error_rate", since_minutes = 60 },
                _ => new { service = "checkout-service", since_hours = 24 }
            };
            return Respond(Call(me.Tool, args));
        }

        return Respond(Call("complete_task", new
        {
            status = "completed",
            summary = me.Tool switch
            {
                "ops__query_logs" => "Logs: 1,342 NullReferenceExceptions in DiscountService.ApplyLoyaltyDiscount (customer.LoyaltyTier null), starting one minute after deploy #4812.",
                "ops__query_metrics" => "Metrics: error rate 18.4% against a 0.2% baseline, rising sharply ~21 minutes ago; p95 latency up 7x; traffic normal, so not load.",
                _ => "Deploys: checkout-service v2.14.0 (deploy #4812, 'Refactor loyalty discounts into DiscountService') went out 22 minutes ago; previous version v2.13.2."
            },
            evidence = new[] { Clip(result, 300) }
        }));
    }

    private static string Report(List<string> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Incident report: checkout-service error spike").AppendLine();
        sb.AppendLine($"_Generated {DateTimeOffset.UtcNow:u} by the incident-response workspace (Mock LLM)._").AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine("Checkout requests started failing with HTTP 500 one minute after deploy #4812 (v2.14.0). The error rate reached 18.4%.").AppendLine();
        sb.AppendLine("## Impact");
        sb.AppendLine("About 18% of checkout attempts failed for ~20 minutes; customers with no loyalty tier could not check out.").AppendLine();
        sb.AppendLine("## Evidence from the investigators");
        foreach (var r in reports) sb.AppendLine($"- {Clip(r.Replace('\n', ' '), 400)}");
        sb.AppendLine().AppendLine("## Likely cause");
        sb.AppendLine("PR #977 moved loyalty discounts into DiscountService.ApplyLoyaltyDiscount, which dereferences customer.LoyaltyTier without a null check.").AppendLine();
        sb.AppendLine("## Proposed fix");
        sb.AppendLine("1. Roll checkout-service back to v2.13.2 now (needs approval).");
        sb.AppendLine("2. Fix the null check, add a test for customers without a tier, and redeploy.").AppendLine();
        sb.AppendLine("## Follow-ups");
        sb.AppendLine("- Alert on error rate per deploy, not only globally.");
        sb.AppendLine("- Canary deploys for checkout.");
        return sb.ToString();
    }

    private static LlmCompletionResponse Respond(ToolCall call, string? content = null) => Respond([call], content);

    private static LlmCompletionResponse Respond(ToolCall[] calls, string? content = null) => new()
    {
        Content = content,
        ToolCalls = calls,
        FinishReason = LlmFinishReason.ToolCalls,
        InputTokens = 600,
        OutputTokens = 60
    };

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..12],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
