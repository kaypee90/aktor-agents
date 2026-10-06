using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// The Mock provider's stand-in for pipelines, so workspaces can be tried with no API key:
/// <list type="bullet">
/// <item>the pipeline editor understands simple requests ("add a reviewer after Draft",
/// "remove Review", "add a fact checker in parallel with Research") and designs a two-stage
/// pipeline for a new workspace;</item>
/// <item>a stage's agent does its step (the output stage also saves a report) and reports;</item>
/// <item>the incident-response template's stages follow <see cref="MockIncidentBehavior"/>.</item>
/// </list>
/// It exercises every pipeline mechanism; it doesn't really reason.
/// </summary>
internal static partial class MockPipelineBehavior
{
    public static bool IsDesignRequest(LlmCompletionRequest request) =>
        request.Tools.Any(t => t.Name == PipelineDesignPrompt.ToolName);

    public static bool IsStageAgent(string system) =>
        system.Contains("You are one stage of a pipeline", StringComparison.Ordinal) ||
        system.Contains("You are a helper started by a pipeline stage", StringComparison.Ordinal);

    // ---- The editor -------------------------------------------------------------------

    public static LlmCompletionResponse Design(LlmCompletionRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var wants = Regex.Match(prompt, @"What the person wants: (.+)").Groups[1].Value.Trim();
        var stages = StageLines().Matches(prompt).Select(m => new StageLine(m.Groups[1].Value, m.Groups[2].Value,
            m.Groups[3].Success ? m.Groups[3].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries) : [])).ToList();

        // "@claude-fast for @diagnose": the mention glossary names the model and the stages.
        var model = Regex.Match(prompt, @"set its model_profile_id to ""([^""]+)""");
        var mentioned = Regex.Matches(prompt, @"^- @\S+: the '[^']+' stage \([^)]*\), stage id ([a-z0-9-]+)\.", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

        object[] changes;
        string summary;
        if (model.Success && mentioned.Count > 0)
        {
            summary = $"Runs {string.Join(", ", mentioned)} on the model '{model.Groups[1].Value}'.";
            changes = [.. mentioned.Select(id => (object)new { op = "update_stage", stage_id = id, stage = new { model_profile_id = model.Groups[1].Value } })];
        }
        else if (stages.Count == 0)
        {
            summary = "A researcher gathers what's needed and a writer turns it into the deliverable.";
            changes =
            [
                new { op = "add_stage", stage = new { stage_id = "research", name = "Research", role = "Researcher", instructions = $"Research what the run's input asks for, for this purpose: {Clip(wants, 300)}. Report the facts and sources the writer will need.", capabilities = new[] { "research" } } },
                new { op = "add_stage", stage = new { stage_id = "write", name = "Write", role = "Writer", instructions = "Turn the research into the deliverable the run's input asks for. Save it as a Markdown file and summarise it.", inputs = new[] { "research" }, capabilities = new[] { "research", "filesystem" } } }
            ];
        }
        else if (RemoveRegex().Match(wants) is { Success: true } remove && Resolve(stages, remove.Groups[1].Value) is { } removed)
        {
            summary = $"Removes '{removed.Name}' and joins its inputs to what it fed.";
            changes = [new { op = "remove_stage", stage_id = removed.Id }];
        }
        else if (AddRegex().Match(wants) is { Success: true } add)
        {
            var name = Title(add.Groups[1].Value);
            var id = PipelineEditor.Slug(name);
            var anchor = Resolve(stages, add.Groups[3].Value) ?? stages[^1];
            var instructions = $"Act as the pipeline's {name.ToLowerInvariant()}: {Clip(wants, 300)}";
            switch (add.Groups[2].Value.ToLowerInvariant())
            {
                case "before":
                    summary = $"Adds '{name}' before '{anchor.Name}'.";
                    changes = [new { op = "add_stage", before = anchor.Id, stage = new { stage_id = id, name, role = name, instructions } }];
                    break;
                case "in parallel with":
                    // Same inputs as the anchor, and into whatever the anchor feeds.
                    summary = $"Adds '{name}' alongside '{anchor.Name}'.";
                    changes =
                    [
                        new { op = "add_stage", stage = new { stage_id = id, name, role = name, instructions, inputs = anchor.Inputs } },
                        .. stages.Where(st => st.Inputs.Contains(anchor.Id)).Select(st => (object)new { op = "connect", from = id, to = st.Id })
                    ];
                    break;
                default:
                    summary = $"Adds '{name}' after '{anchor.Name}'.";
                    changes = [new { op = "add_stage", after = anchor.Id, stage = new { stage_id = id, name, role = name, instructions } }];
                    break;
            }
        }
        else
        {
            summary = $"Adds a reviewer after '{stages[^1].Name}' (Mock LLM: say \"add a … after …\" or \"remove …\" for other changes).";
            changes = [new { op = "add_stage", after = stages[^1].Id, stage = new { name = "Reviewer", role = "Reviewer", instructions = $"Review the result before it's delivered: {Clip(wants, 300)}" } }];
        }

        return Respond(Call(PipelineDesignPrompt.ToolName, new { summary, changes }));
    }

    private sealed record StageLine(string Id, string Name, string[] Inputs);

    private static StageLine? Resolve(List<StageLine> stages, string text)
    {
        var t = text.Trim().Trim('"', '\'', '.', '@').ToLowerInvariant();
        return stages.FirstOrDefault(s => s.Id == t || s.Name.Equals(t, StringComparison.OrdinalIgnoreCase))
               ?? stages.FirstOrDefault(s => t.Contains(s.Name.ToLowerInvariant()) || t.Contains(s.Id));
    }

    /// <summary>A stage line of the editor's prompt: - id "Name" (Role) ← a, b: ...</summary>
    [GeneratedRegex(@"^- ([a-z0-9-]+) ""([^""]+)"" \([^)]*\)(?: ← ([a-z0-9, -]+))?", RegexOptions.Multiline)]
    private static partial Regex StageLines();

    [GeneratedRegex(@"\b(?:remove|delete|drop)\s+(?:the\s+)?(.+?)(?:\s+stage|\s+agent)?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RemoveRegex();

    [GeneratedRegex(@"\badd\s+(?:a|an|another)?\s*(.+?)(?:\s+stage|\s+agent)?\s+(after|before|in parallel with)\s+(?:the\s+)?(.+?)(?:\s+stage|\s+agent)?(?:[,.].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddRegex();

    // ---- Stage agents -----------------------------------------------------------------

    public static LlmCompletionResponse Stage(LlmCompletionRequest request, string system)
    {
        if (MockIncidentBehavior.TryRespond(request, system) is { } incident) return incident;

        var role = Regex.Match(system, @"acting as: (.+?)\.\n").Groups[1].Value;
        var kickoff = request.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var tools = request.Tools.Select(t => t.Name).ToHashSet();
        var called = request.Messages.Where(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!).Select(c => c.Name).ToList();

        var input = Section(kickoff, "## This run's input", "##") ?? Clip(kickoff, 300);
        var isOutput = kickoff.Contains("Your summary is this run's result", StringComparison.Ordinal);
        var file = $"{PipelineEditor.Slug(role)}.md";
        if (isOutput && tools.Contains("filesystem_write") && !called.Contains("filesystem_write"))
        {
            var earlier = Section(kickoff, "## Results from the stages before you", "## How to work") ?? "(none)";
            return Respond(Call("filesystem_write", new
            {
                path = file,
                content = $"# {role}\n\n_Mock LLM: connect a real model for real work._\n\n## Input\n{input.Trim()}\n\n## From earlier stages\n{earlier.Trim()}\n"
            }));
        }

        return Respond(Call("complete_task", new
        {
            status = "completed",
            summary = $"{role} (mock): handled \"{Clip(input.Trim().Replace('\n', ' '), 160)}\"." + (isOutput ? $" See {file}." : ""),
            artifacts = isOutput && called.Contains("filesystem_write") ? new[] { file } : Array.Empty<string>()
        }));
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static string? Section(string text, string header, string next)
    {
        var start = text.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return null;
        start = text.IndexOf('\n', start) + 1;
        var end = text.IndexOf(next, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string Title(string s) =>
        string.Join(' ', s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    internal static LlmCompletionResponse Respond(ToolCall call, string? content = null) => new()
    {
        Content = content,
        ToolCalls = [call],
        FinishReason = LlmFinishReason.ToolCalls,
        InputTokens = 600,
        OutputTokens = 60
    };

    internal static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..12],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    internal static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
