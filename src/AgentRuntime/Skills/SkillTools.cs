using System.Text.Json;
using AgentRuntime.LLM;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Skills;

/// <summary>
/// load_skill: an agent reads a skill's full instructions (and the list of its resource files) when
/// the skill is relevant. Read-only and scoped to the agent's own organization.
/// </summary>
public sealed class LoadSkillTool(ISkillStore skills) : ITool
{
    public const string Name = "load_skill";

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name,
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Load one of your organization's skills (listed under SKILLS in your instructions): its full " +
                      "instructions and the resource files it comes with. Load a skill when the work matches its description, then follow it.",
        JsonSchema = """{ "type": "object", "properties": { "name": { "type": "string", "description": "The skill's name, as listed" } }, "required": ["name"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var name = Arg(request.ArgumentsJson, "name");
        var skill = name is null ? null : await skills.ResolveAsync(request.TenantId, name.Trim(), WorkspaceOf(request), request.CancellationToken);
        if (skill is null)
        {
            return ToolExecutionResult.Fail($"No skill named '{name}'. Use one of the names listed under SKILLS.");
        }

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            name = skill.Name,
            version = skill.Version,
            instructions = skill.Instructions,
            files = skill.Files.Select(f => new { path = f.Path, chars = f.Content.Length }),
            note = skill.Files.Count == 0 ? null : "Read a file with read_skill_file when the instructions point to it."
        }, ToolJson.Options));
    }

    /// <summary>Workspace agents use their workspace's id as their task id: its skills are theirs too.</summary>
    internal static string? WorkspaceOf(ToolExecutionRequest request) =>
        Workspaces.WorkspaceIds.IsWorkspace(request.TaskId) ? request.TaskId : null;

    internal static string? Arg(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>read_skill_file: one resource file of a skill (reference material, a template, a script to adapt).</summary>
public sealed class ReadSkillFileTool(ISkillStore skills) : ITool
{
    public const string Name = "read_skill_file";
    private const int MaxChars = 60_000;

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name,
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Read a resource file that comes with a skill (see load_skill's file list).",
        JsonSchema = """{ "type": "object", "properties": { "name": { "type": "string", "description": "The skill's name" }, "path": { "type": "string", "description": "The file's path within the skill" } }, "required": ["name", "path"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var name = LoadSkillTool.Arg(request.ArgumentsJson, "name");
        var path = SkillPackage.NormalizePath(LoadSkillTool.Arg(request.ArgumentsJson, "path") ?? string.Empty);
        var skill = name is null ? null : await skills.ResolveAsync(request.TenantId, name.Trim(), LoadSkillTool.WorkspaceOf(request), request.CancellationToken);
        if (skill is null) return ToolExecutionResult.Fail($"No skill named '{name}'.");

        var file = skill.Files.FirstOrDefault(f => f.Path == path);
        if (file is null)
        {
            return ToolExecutionResult.Fail($"'{name}' has no file '{path}'. Its files: {string.Join(", ", skill.Files.Select(f => f.Path))}.");
        }

        var truncated = file.Content.Length > MaxChars;
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            name = skill.Name,
            path = file.Path,
            content = truncated ? file.Content[..MaxChars] : file.Content,
            truncated
        }, ToolJson.Options));
    }
}

/// <summary>The SKILLS prompt section: each enabled skill's name and description, so the agent
/// knows what it can load. Only the list is in the prompt; instructions load on demand.</summary>
public sealed class SkillsSection(IOptions<SkillOptions> options) : ISystemPromptSection
{
    public string Header => "SKILLS";

    public string Render(AgentPromptContext context)
    {
        if (context.State.IsResident || context.Skills.Count == 0) return string.Empty;

        // Skills a person named in this agent's goal or context (@skill:name) come first and are required.
        var named = Pipelines.Mentions.SkillsIn([context.State.Goal, context.State.Metadata.GetValueOrDefault("initial_context")]);
        var asked = context.Skills.Where(s => named.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var listed = asked.Concat(context.Skills.Except(asked).Take(Math.Max(0, options.Value.MaxListedInPrompt - asked.Count))).ToList();
        var lines = listed.Select(s => $"- {s.Name}: {s.Description}" + (asked.Contains(s) ? " [ASKED FOR]" : ""));
        var more = context.Skills.Count > listed.Count ? $"\n({context.Skills.Count - listed.Count} more skills can be loaded by name.)" : string.Empty;
        var required = asked.Count > 0
            ? $"\nYour instructions name {string.Join(", ", asked.Select(s => $"@skill:{s.Name}"))}: load {(asked.Count == 1 ? "that skill" : "those skills")} with load_skill before anything else, and follow {(asked.Count == 1 ? "it" : "them")}."
            : string.Empty;
        return "Your organization (and your workspace, if you're in one) has written these skills: proven ways to do particular kinds of work. When your work " +
               "matches a skill's description, call load_skill with its name before you start, and follow it. Skills are " +
               "instructions, not permissions: they never give you tools, budget or access you don't have.\n" +
               string.Join("\n", lines) + more + required;
    }
}
