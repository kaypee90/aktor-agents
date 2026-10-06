using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's script for the incident-response template's pipeline (roadmap P9), so the
/// flagship demo runs end to end with no API key: Triage reads the alert → three investigators
/// (logs, metrics, deploys) each query the ops connection and report → Diagnose writes
/// incident-report.md → Remediate proposes a rollback (which waits for approval) and reports. It
/// reads only the transcript, like every Mock behavior; a real model follows the stages' instructions.
/// </summary>
internal static class MockIncidentBehavior
{
    private static readonly (string Role, string Tool, string Focus)[] Investigators =
    [
        ("Logs investigator", "ops__query_logs", "error logs"),
        ("Metrics investigator", "ops__query_metrics", "error rate and latency"),
        ("Deploy investigator", "ops__list_deploys", "recent deploys")
    ];

    public static LlmCompletionResponse? TryRespond(LlmCompletionRequest request, string system)
    {
        var tools = request.Tools.Select(t => t.Name).ToHashSet();
        bool Is(string role) => system.Contains($"acting as: {role}.", StringComparison.Ordinal);

        var investigator = Investigators.FirstOrDefault(i => Is(i.Role));
        if (investigator.Role is not null && tools.Contains(investigator.Tool)) return Investigate(request, investigator);
        if (Is("Incident triager")) return Triage(request);
        if (Is("Incident analyst")) return Diagnose(request, tools);
        if (Is("Remediator") && tools.Contains("ops__rollback_deploy")) return Remediate(request);
        return null;
    }

    private static LlmCompletionResponse Triage(LlmCompletionRequest request)
    {
        var input = request.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var service = Regex.Match(input, "\"service\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } s ? s : "checkout-service";
        return Respond(Call("complete_task", new
        {
            status = "completed",
            summary = $"Service: {service}. Symptom: HighErrorRate, 18.4% against a 2% threshold. Severity: critical. " +
                      "Time window: the last 60 minutes. Not noise: a real, sustained spike."
        }));
    }

    private static LlmCompletionResponse Diagnose(LlmCompletionRequest request, HashSet<string> tools)
    {
        var kickoff = request.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var wrote = request.Messages.Any(m => m.Role == ChatRole.Tool && m.ToolName == "filesystem_write");
        if (!wrote && tools.Contains("filesystem_write"))
        {
            return Respond(Call("filesystem_write", new { path = "incident-report.md", content = Report(Findings(kickoff)) }));
        }

        return Respond(Call("complete_task", new
        {
            status = "completed",
            summary = "Likely cause: deploy #4812 (v2.14.0) of checkout-service, which introduced a NullReferenceException in " +
                      "DiscountService.ApplyLoyaltyDiscount; errors began one minute after it. Roll back to v2.13.2, then fix the null check.",
            artifacts = new[] { "incident-report.md" }
        }));
    }

    private static LlmCompletionResponse Remediate(LlmCompletionRequest request)
    {
        var rollback = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == "ops__rollback_deploy")?.Content;
        if (rollback is null)
        {
            return Respond(Call("ops__rollback_deploy", new
            {
                service = "checkout-service",
                to_version = "v2.13.2",
                reason = "Errors began one minute after deploy #4812 (v2.14.0): NullReferenceException in DiscountService.ApplyLoyaltyDiscount."
            }), "The evidence points at deploy #4812 (v2.14.0). I propose rolling checkout-service back to v2.13.2; this needs approval.");
        }

        var rolledBack = rollback.Contains("\"rolled_back\":true", StringComparison.Ordinal);
        return Respond(Call("complete_task", new
        {
            status = rolledBack ? "completed" : "partial",
            summary = "Incident: checkout-service error rate rose to 18.4% one minute after deploy #4812 (v2.14.0), from a " +
                      "NullReferenceException in DiscountService.ApplyLoyaltyDiscount. " +
                      (rolledBack ? "With approval, it was rolled back to v2.13.2. " : "The rollback to v2.13.2 did not run: " + Clip(rollback, 160) + " ") +
                      "Full write-up: incident-report.md.",
            artifacts = new[] { "incident-report.md" },
            remaining_work = rolledBack ? new[] { "Fix the null check and redeploy." } : new[] { "Roll back or fix checkout-service." }
        }));
    }

    /// <summary>The investigators' results, as the run handed them to this stage.</summary>
    private static List<string> Findings(string kickoff) =>
        Regex.Matches(kickoff, @"### (.+?)\n(.+?)\n", RegexOptions.Singleline).Select(m => $"{m.Groups[1].Value}: {m.Groups[2].Value.Trim()}").ToList();

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

    private static string Report(List<string> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Incident report: checkout-service error spike").AppendLine();
        sb.AppendLine($"_Generated {DateTimeOffset.UtcNow:u} by the incident-response pipeline (Mock LLM)._").AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine("Checkout requests started failing with HTTP 500 one minute after deploy #4812 (v2.14.0). The error rate reached 18.4%.").AppendLine();
        sb.AppendLine("## Impact");
        sb.AppendLine("About 18% of checkout attempts failed for ~20 minutes; customers with no loyalty tier could not check out.").AppendLine();
        sb.AppendLine("## Evidence from the investigators");
        foreach (var f in findings) sb.AppendLine($"- {Clip(f.Replace('\n', ' '), 400)}");
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
