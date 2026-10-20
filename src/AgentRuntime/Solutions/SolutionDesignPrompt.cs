using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Tools;

namespace AgentRuntime.Solutions;

/// <summary>A skill as the builder holds it: SKILL.md's parts, not yet a file.</summary>
public sealed record DraftSkill
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public string? Workspace { get; init; }
}

/// <summary>One turn of the conversation with the solution designer, as the builder keeps it.</summary>
public sealed record DesignTurn(string Role, string Content);

/// <summary>
/// One change the designer proposes to a solution draft: put (create or replace) or remove one
/// item of a section. Items are whole, in the manifest's own snake_case shape, so what the model
/// writes is exactly what the builder and the CLI read.
/// </summary>
public sealed record SolutionEditOp
{
    /// <summary>"set" or "remove".</summary>
    public string Op { get; init; } = "set";
    /// <summary>basics, input, connector, entity, workspace, skill, knowledge, assistant, end_users, seed, webhook or eval.</summary>
    public string Section { get; init; } = string.Empty;
    /// <summary>Which item: an input key, connector name, entity name, workspace key, skill name,
    /// knowledge key, seed entity, webhook URL or eval name. Unused for basics, assistant and end_users.</summary>
    public string? Key { get; init; }
    public JsonObject? Value { get; init; }
}

/// <summary>What the organization has that a solution can use, for the designer to choose from.</summary>
public sealed record SolutionDesignContext
{
    /// <summary>Installed plugins: id, name, and their non-secret setting keys.</summary>
    public IReadOnlyList<(string Id, string Name, string Settings)> Plugins { get; init; } = [];
    /// <summary>The organization's global connectors: name and plugin id.</summary>
    public IReadOnlyList<(string Name, string PluginId)> GlobalConnectors { get; init; } = [];
}

/// <summary>
/// The solution designer: one LLM call that turns "add a Vendor entity and let the triage workspace
/// assign tickets to vendors" (or a description of a whole app) into <see cref="SolutionEditOp"/>s.
/// The model only proposes; <see cref="SolutionDraftEditor"/> applies the edits, the server validates
/// the result like any package, and a person applies it in the builder.
/// </summary>
public static class SolutionDesignPrompt
{
    public const string ToolName = "propose_solution_changes";

    /// <summary>Recognisable in the system prompt (the Mock provider answers it heuristically).</summary>
    public const string SystemMarker = "You design Aktor solutions.";

    public const int MaxDraftChars = 40_000;
    public const int MaxHistoryTurns = 10;

    public static readonly string[] Sections = ["basics", "input", "connector", "entity", "workspace", "skill", "knowledge", "assistant", "end_users", "seed", "webhook", "eval"];

    public static readonly LlmToolDefinition Tool = new()
    {
        Name = ToolName,
        Description = "Reply to the person and propose the changes to the solution that do what they asked. Propose no changes when you need to ask them something first.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "reply": { "type": "string", "description": "What you'd say to the person: what the changes do and why, or the question you need answered. Two to five sentences, plain language." },
            "summary": { "type": "string", "description": "One line naming the changes, e.g. 'Adds a Vendor entity and vendor assignment to triage'." },
            "changes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "op": { "type": "string", "enum": ["set", "remove"] },
                  "section": { "type": "string", "enum": ["basics", "input", "connector", "entity", "workspace", "skill", "knowledge", "assistant", "end_users", "seed", "webhook", "eval"] },
                  "key": { "type": "string", "description": "Which item (see the format). Not used for basics, assistant or end_users." },
                  "value": { "type": "object", "description": "set: the whole item in the manifest's format (for basics, only the fields to change)." }
                },
                "required": ["op", "section"]
              }
            }
          },
          "required": ["reply", "changes"]
        }
        """
    };

    private const string Format = """
        A solution is an app built on the Aktor agent platform. Its package (aktor.yaml, here as JSON,
        snake_case) has these sections, and you edit them one item at a time:

        - basics: { name (lowercase-dashed id), title, version (semver), description }. Set only fields to change.
        - input (key = input key): { key, label, description, type: string|text|number|integer|boolean, default, required }.
          Values asked for at install. Workspaces read them as {{ vars.key }}; connector settings, webhook URLs
          and the assistant's instructions and greeting read them as {{ inputs.key }}.
        - connector (key = name, lowercase_underscore): { name, plugin (a plugin id below), description,
          settings: { key: value }, required, global (optional: the name of one of the organization's global
          connectors to use instead of asking for secrets) }. Its tools appear to agents as <name>__<tool>.
          Never put secrets in settings; they're asked for at install.
        - entity (key = PascalCase name): { description, title_field, fields: { snake_case_field: { type, required,
          description, values (enum), entity (ref), default, indexed } } }. Field types: string, text, integer,
          number, boolean, date, datetime, enum, ref (to another entity), end_user (the customer a record belongs
          to; set by the runtime), json. Agents get tools data__list_<entity>, data__get_<entity>, and with write
          access data__create_<entity>, data__update_<entity>, data__delete_<entity> (entity in snake_case,
          e.g. WorkOrder → work_order).
        - workspace (key = lowercase-dashed key): { key, name, goal, data: { Entity: read|write }, connectors:
          [connector names], daily_cost_usd, safety: { autonomy: Supervised|SemiAutonomous|Autonomous, rules:
          [{ name, tool_pattern (a glob, e.g. "sms__*"), applies: Any|Writes|Unsafe (exactly one of these: Any is
          every call, Writes is calls that change something, Unsafe is sends, payments and deletes), decision:
          Allow|RequireApproval|Deny }] }, variables: {},
          pipeline: { stages: [ ... ] }, triggers: [ ... ] }.
          An agent stage: { stage_id, name, role, instructions, inputs: [stage ids it waits for], capabilities:
          [...] }. An action stage (one tool call, no model): { stage_id, name, kind: "Action", inputs, tool:
          "<connection>__<tool>", arguments: { ... } } where arguments may use {{ input.record.<field> }},
          {{ input.record_id }} and {{ stages.<id>.result }}.
          Triggers: { kind: "Record", name, entity, on: [created|updated|deleted], rule?: { conditions: [{ field,
          op: ==|!=|<|<=|>|>=|contains|exists, value }] }, instruction } runs the pipeline on a record change;
          { kind: "Schedule", name, cron: "0 8 * * 1", instruction }; { kind: "Webhook", name, instruction }.
        - skill (key = lowercase-dashed name): { name, description (when to use it), instructions (markdown),
          workspace (a workspace key, or null for every workspace) }. How-to knowledge agents follow.
        - knowledge (key = a title): { key, text (markdown), workspace (optional) }. Reference text agents search.
        - assistant: { name, greeting, instructions, data: { Entity: read|write }, connectors: [...],
          max_conversations_per_day, max_messages_per_minute, max_cost_usd }. The agent the solution's own customers
          chat with; records with an end_user field are limited to that customer's own. Instructions say which
          data__ tools to use for what.
        - end_users: { jwt: { issuer, audience, jwks_url, user_claim } }: the builder's own sign-in (rarely needed).
        - seed (key = entity): { records: [ { id (a local name other seed records can ref), field: value } ] }:
          demo or reference records created at first install.
        - webhook (key = url): { url, events: [record.created|record.updated|record.deleted|record.*], entities: [] }.
        - eval (key = name): { name, workspace (key), input (the run's input text), input_data (optional object),
          expect: { status: Completed, summary_contains: [...], records: { Entity: { at_least, at_most, where } } } }.

        @mentions: a workspace's goal, its stages' instructions and its triggers' instructions, the assistant's
        instructions and skills' instructions can name things with @: @<stage_id> (a stage of the same
        pipeline), @skill:<name> (the agent loads that skill first), @knowledge:<handle> (it reads that
        knowledge first; the handle is the key lowercased, spaces as dashes) and @connector:<name> (it uses that
        connector's tools; list the connector in the workspace's or assistant's connectors too). When an
        instruction relies on a skill, knowledge or connector, mention it. The person's message may mention
        things the same way, and @workspace:<key> or @assistant; what each one refers to is listed with it.

        How to design well:
        - Model the business: the things people track become entities, with the fields they'd filter by.
        - One workspace per job that runs on its own (triage, renewals, a weekly digest). Start runs with record
          triggers for things that happen to data, schedules for routine work.
        - Pipelines: one agent stage per distinct job; most workspaces need one or two stages. Instructions say
          what to do with the input, which data__ tools to use, and what to report, in two to five sentences.
        - Give each workspace and the assistant the least data access that works (read unless it writes).
        - Add an eval for each workspace's main behaviour once it exists.
        - Change only what was asked; keep keys and names of existing items. When you set an item, write it
          whole: the old one is replaced. When you rename an entity or workspace, also update what refers to it.
        - If the request is unclear or would need a decision only the person can make, ask in "reply" and propose
          no changes. Otherwise act; don't ask for permission.
        """;

    public static LlmCompletionRequest BuildRequest(JsonObject draft, IReadOnlyList<DraftSkill> skills, IReadOnlyList<DesignTurn> history,
        string request, SolutionDesignContext context, string? model, int maxOutputTokens)
    {
        var system = new StringBuilder();
        system.AppendLine(SystemMarker);
        system.AppendLine(Format);
        system.AppendLine($"Capabilities a stage can have: {string.Join(", ", AgentToolCatalog.KnownCapabilities)}.");
        if (context.Plugins.Count > 0)
        {
            system.AppendLine("Plugins installed on this server (connector plugin ids): " +
                              string.Join("; ", context.Plugins.Select(p => $"{p.Id} ({p.Name}{(p.Settings.Length > 0 ? $"; settings: {p.Settings}" : "")})")) + ".");
        }

        system.AppendLine(context.GlobalConnectors.Count > 0
            ? "The organization's global connectors (already set up, with their secrets; prefer them by setting a connector's \"global\"): " +
              string.Join("; ", context.GlobalConnectors.Select(g => $"{g.Name} = {g.PluginId}")) + "."
            : "The organization has no global connectors yet.");

        var messages = new List<ChatMessage> { new() { Role = ChatRole.System, Content = system.ToString() } };
        foreach (var turn in history.TakeLast(MaxHistoryTurns))
        {
            if (string.IsNullOrWhiteSpace(turn.Content)) continue;
            messages.Add(new ChatMessage { Role = turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, Content = Clip(turn.Content, 2000) });
        }

        var user = new StringBuilder();
        user.AppendLine("The solution now (the builder's draft):");
        user.AppendLine(Clip(draft.ToJsonString(), MaxDraftChars));
        if (skills.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("Its skills:");
            foreach (var s in skills) user.AppendLine($"- {s.Name}{(s.Workspace is { Length: > 0 } w ? $" (workspace {w})" : "")}: {Clip(s.Description, 200)}");
        }

        var mentions = DescribeMentions(request, draft, skills, context);
        user.AppendLine();
        user.AppendLine($"What the person wants: {request.Trim()}");
        if (mentions.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("What the @mentions in it refer to:");
            foreach (var line in mentions) user.AppendLine($"- {line}");
        }
        user.AppendLine();
        user.AppendLine($"Call {ToolName} with your reply and the changes.");
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = user.ToString() });

        return new LlmCompletionRequest
        {
            Messages = messages,
            Tools = [Tool],
            Model = model,
            MaxTokens = maxOutputTokens,
            Temperature = 0.2
        };
    }

    /// <summary>
    /// One line per @mention in the person's message that names something: an item of the draft (a
    /// stage, workspace, the assistant, a skill, knowledge, a connector) or one of the organization's
    /// global connectors not in it yet. The builder offers exactly these as you type.
    /// </summary>
    public static List<string> DescribeMentions(string request, JsonObject draft, IReadOnlyList<DraftSkill> skills, SolutionDesignContext context)
    {
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
        static IEnumerable<JsonObject> Items(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>() : [];
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        var workspaces = Items(draft["workspaces"]).ToList();
        var lines = new List<string>();
        foreach (var handle in Mentions.Find(request))
        {
            if (TryAfter(handle, Mentions.SkillPrefix, out var skillName))
            {
                lines.Add(skills.FirstOrDefault(s => Same(s.Name, skillName)) is { } skill
                    ? $"@{handle}: the solution's skill '{skill.Name}' ({skill.Description}){(skill.Workspace is { Length: > 0 } w ? $", for workspace {w}" : "")}."
                    : $"@{handle}: a skill named '{skillName}', not in the solution: one of the organization's own skills.");
            }
            else if (TryAfter(handle, Mentions.KnowledgePrefix, out var knowledgeHandle))
            {
                var entry = Items(draft["knowledge"]).FirstOrDefault(k => Same(Memory.KnowledgeFiles.Handle(Str(k["key"])), knowledgeHandle));
                lines.Add(entry is not null
                    ? $"@{handle}: the solution's knowledge '{Str(entry["key"])}'."
                    : $"@{handle}: knowledge '{knowledgeHandle}', not in the solution: one of the organization's documents or facts.");
            }
            else if (TryAfter(handle, Mentions.ConnectorPrefix, out var connectorName))
            {
                var connector = Items(draft["connectors"]).FirstOrDefault(c => Same(Str(c["name"]), connectorName));
                if (connector is not null)
                {
                    lines.Add($"@{handle}: the solution's connector '{Str(connector["name"])}' (plugin {Str(connector["plugin"])}); its tools are {Str(connector["name"])}__<tool>.");
                }
                else if (context.GlobalConnectors.FirstOrDefault(g => Same(g.Name, connectorName)) is { Name: not null } org)
                {
                    lines.Add($"@{handle}: the organization's global connector '{org.Name}' (plugin {org.PluginId}), not in the solution yet: " +
                              $"to use it, add a connector with plugin \"{org.PluginId}\" and global \"{org.Name}\".");
                }
            }
            else if (TryAfter(handle, Mentions.WorkspacePrefix, out var key))
            {
                if (workspaces.FirstOrDefault(w => Same(Str(w["key"]), key)) is { } w) lines.Add($"@{handle}: the workspace '{Str(w["name"])}' (key {Str(w["key"])}).");
            }
            else if (Same(handle, "assistant"))
            {
                if (draft["assistant"] is JsonObject) lines.Add("@assistant: the solution's assistant, the agent its end users talk to.");
            }
            else
            {
                var stages = workspaces
                    .SelectMany(w => Items(w["pipeline"]?["stages"]).Where(s => Same(Str(s["stage_id"]), handle)).Select(s => $"the '{Str(s["name"])}' stage of workspace {Str(w["key"])}"))
                    .ToList();
                if (stages.Count > 0) lines.Add($"@{handle}: {string.Join("; or ", stages)}.");
            }
        }

        return lines;
    }

    private static bool TryAfter(string handle, string prefix, out string rest)
    {
        var ok = handle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && handle.Length > prefix.Length;
        rest = ok ? handle[prefix.Length..] : string.Empty;
        return ok;
    }

    public sealed record Proposal(string Reply, string Summary, List<SolutionEditOp> Ops);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>What the model proposed; null with a reason if it didn't call the tool or wrote nonsense.
    /// A reply with no changes (a question) is a proposal.</summary>
    public static (Proposal? Proposal, string? Error) Parse(LlmCompletionResponse response)
    {
        var call = response.ToolCalls?.FirstOrDefault(c => c.Name == ToolName);
        if (call is null)
        {
            // Some models answer in prose when they only have a question.
            return string.IsNullOrWhiteSpace(response.Content)
                ? (null, "The model proposed nothing.")
                : (new Proposal(Clip(response.Content, 2000), string.Empty, []), null);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ProposalJson>(call.ArgumentsJson, Json);
            if (parsed is null) return (null, "The model proposed nothing.");
            var ops = (parsed.Changes ?? []).Select(c => new SolutionEditOp
            {
                Op = (c.Op ?? "set").Trim().ToLowerInvariant(),
                Section = (c.Section ?? string.Empty).Trim().ToLowerInvariant(),
                Key = c.Key?.Trim(),
                Value = c.Value
            }).ToList();
            return (new Proposal(parsed.Reply?.Trim() ?? string.Empty, parsed.Summary?.Trim() ?? string.Empty, ops), null);
        }
        catch (JsonException ex)
        {
            return (null, $"The model's proposal wasn't valid: {ex.Message}");
        }
    }

    private sealed record ProposalJson(string? Reply, string? Summary, List<ChangeJson>? Changes);

    private sealed record ChangeJson(string? Op, string? Section, string? Key, JsonObject? Value);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
