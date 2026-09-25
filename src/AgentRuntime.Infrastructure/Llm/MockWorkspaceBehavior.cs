using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's stand-in for workspace agents, so workspaces can be tried with no API key.
/// The coordinator turns ongoing requests ("monitor…", "every…", "alert me…") into a standing
/// monitor agent with a schedule, and answers everything else. Monitors report when their
/// schedule or webhook fires. It exercises every workspace mechanism; it doesn't really reason.
/// </summary>
internal static partial class MockWorkspaceBehavior
{
    public static LlmCompletionResponse Respond(LlmCompletionRequest request)
    {
        var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
        var isCoordinator = system.Contains("acting as: Coordinator.", StringComparison.Ordinal);
        var tools = request.Tools.Select(t => t.Name).ToHashSet();

        // Everything since the latest input is what this turn has already done.
        var lastInputIndex = request.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);
        var input = lastInputIndex >= 0 ? request.Messages[lastInputIndex].Content ?? string.Empty : string.Empty;
        var doneThisTurn = request.Messages.Skip(lastInputIndex + 1)
            .Where(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Name)
            .ToList();

        if (doneThisTurn.Count > 0)
        {
            if (isCoordinator && doneThisTurn.Contains("spawn_agent") && !doneThisTurn.Contains("notify_user"))
            {
                var spawned = SpawnedAgentId(request);
                return Respond(Call("notify_user", new
                {
                    text = spawned is not null
                        ? $"I've set up a standing Monitor Agent ({spawned}) for this: \"{Excerpt(UserText(input), 160)}\". " +
                          "It will check on a schedule and tell you when something needs attention. (Mock LLM: connect a real model for real work.)"
                        : $"Your existing Monitor Agent will cover this: \"{Excerpt(UserText(input), 160)}\". (Mock LLM: connect a real model for real work.)"
                }));
            }

            if (!isCoordinator && doneThisTurn.Contains("create_schedule") && !doneThisTurn.Contains("notify_user"))
            {
                return Respond(Call("notify_user", new { text = "Monitoring is set up. I'll report when something needs attention." }));
            }

            return Respond(Wait(isCoordinator ? "Waiting for the user's next request." : "Waiting for the next check."));
        }

        if (!tools.Contains("wait_for_events") && tools.Contains("complete_task"))
        {
            // A one-shot worker: do the job, report back.
            return Respond(Call("complete_task", new { status = "completed", summary = $"Done (mock): {Excerpt(input, 160)}" }));
        }

        if (isCoordinator)
        {
            if (input.Contains("Your child agent", StringComparison.Ordinal))
            {
                return Respond(Call("notify_user", new { text = $"Update: {Excerpt(input, 300)}" }));
            }

            var text = UserText(input);
            if (OngoingRegex().IsMatch(text) && tools.Contains("spawn_agent"))
            {
                return Respond(Call("spawn_agent", new
                {
                    role = "Monitor Agent",
                    goal = text,
                    standing = true,
                    capabilities = new[] { "research" }
                }));
            }

            return Respond(Call("notify_user", new { text = $"Noted: \"{Excerpt(text, 200)}\". (Mock LLM: connect a real model for real work.)" }));
        }

        // Standing agent.
        if (input.Contains("[Scheduled trigger", StringComparison.Ordinal))
        {
            return Respond(Call("notify_user", new { text = $"Scheduled check done at {DateTimeOffset.UtcNow:HH:mm} UTC (mock): no issues found." }));
        }

        if (input.Contains("[Webhook", StringComparison.Ordinal))
        {
            var payload = input[(input.IndexOf("Payload", StringComparison.Ordinal) is var i and >= 0 ? i : 0)..];
            return Respond(Call("notify_user", new { text = $"Received a webhook event: {Excerpt(payload, 200)}", urgency = "warning" }));
        }

        if (input.Contains("Your goal:", StringComparison.Ordinal) && tools.Contains("create_schedule"))
        {
            return Respond(Call("create_schedule", new
            {
                name = "Periodic check",
                instruction = "Run the check described in your goal and report anything that needs attention.",
                every_minutes = IntervalMinutes(input)
            }));
        }

        return Respond(Wait("Nothing to do for this event."));
    }

    private static double IntervalMinutes(string text)
    {
        var m = IntervalRegex().Match(text);
        if (!m.Success) return 60;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? n * 60 : n;
    }

    private static string? SpawnedAgentId(LlmCompletionRequest request)
    {
        var result = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == "spawn_agent")?.Content;
        if (result is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.TryGetProperty("agent_id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The user's words without the runtime's framing lines.</summary>
    private static string UserText(string input)
    {
        const string purpose = "Its purpose: ";
        var p = input.IndexOf(purpose, StringComparison.Ordinal);
        if (p >= 0)
        {
            var rest = input[(p + purpose.Length)..];
            var end = rest.IndexOf('\n');
            return (end >= 0 ? rest[..end] : rest).Trim();
        }

        var lines = input.Split('\n').Where(l => !l.StartsWith('[') && !l.StartsWith("Your goal:") && !l.StartsWith("Initial context:") && !l.StartsWith("Begin working")).ToList();
        var text = string.Join(' ', lines).Trim();
        return text.Length > 0 ? text : input.Trim();
    }

    private static LlmCompletionResponse Respond(ToolCall call) => new()
    {
        ToolCalls = [call],
        FinishReason = LlmFinishReason.ToolCalls,
        InputTokens = 500,
        OutputTokens = 40
    };

    private static ToolCall Wait(string summary) => Call("wait_for_events", new { summary });

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..12],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static string Excerpt(string s, int max)
    {
        var flat = s.Replace('\n', ' ').Trim();
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    [GeneratedRegex(@"\b(monitor|watch|track|alert|every|daily|hourly|notify me|keep an eye|check)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OngoingRegex();

    [GeneratedRegex(@"every\s+(\d+(?:\.\d+)?)\s*(minute|min|hour|hr)s?", RegexOptions.IgnoreCase)]
    private static partial Regex IntervalRegex();
}
