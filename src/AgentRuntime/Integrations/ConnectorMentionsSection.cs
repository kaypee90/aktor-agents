namespace AgentRuntime.Integrations;

/// <summary>Connectors a person named in this agent's goal or context (<c>@connector:slack</c>):
/// the agent is told which of its tools those are, or that it doesn't have them, so a mention means
/// "use that integration" however capable the model is. A mention never gives an agent a connection
/// it wasn't given.</summary>
public sealed class ConnectorMentionsSection : LLM.ISystemPromptSection
{
    public string Header => "CONNECTORS YOU WERE POINTED TO";

    public string Render(LLM.AgentPromptContext context)
    {
        var named = Pipelines.Mentions.ConnectorsIn([context.State.Goal, context.State.Metadata.GetValueOrDefault("initial_context")]);
        if (named.Count == 0) return string.Empty;
        var lines = named.Select(name =>
        {
            var prefix = name + "__";
            var tools = context.AvailableTools.Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToList();
            return tools.Count > 0
                ? $"- @connector:{name}: use its tools ({string.Join(", ", tools.Take(12))}{(tools.Count > 12 ? ", …" : "")}) for that part of your work."
                : $"- @connector:{name}: you have no {prefix} tools, so it isn't connected here. Say so in your result instead of working around it.";
        });
        return "Your instructions name these connectors:\n" + string.Join("\n", lines);
    }
}
