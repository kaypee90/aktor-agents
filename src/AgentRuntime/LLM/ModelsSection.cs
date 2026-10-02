using System.Globalization;

namespace AgentRuntime.LLM;

/// <summary>
/// The MODELS prompt section (docs/llm-settings.md): the organization's models an agent can give
/// the agents it spawns, with what each is for and what it costs, so a goal like "use the cheap
/// model for data collection" can be followed. Shown only to agents that can spawn, and only when
/// the organization lets agents choose.
/// </summary>
public sealed class ModelsSection : ISystemPromptSection
{
    public string Header => "MODELS";

    public string Render(AgentPromptContext context)
    {
        if (context.Models.Count == 0 || context.AvailableTools.All(t => t.Name != "spawn_agent")) return string.Empty;

        var lines = context.Models.Select(m =>
            $"- {m.Id}: {m.Name}{(m.Provider == "Mock" ? " (demo)" : $" ({m.Provider} {m.Model})")}, " +
            $"{Price(m.InPerMillion)} in / {Price(m.OutPerMillion)} out per million tokens" +
            (string.IsNullOrWhiteSpace(m.Description) ? string.Empty : $". Use for: {m.Description}"));

        return $"You run on {context.CurrentModel ?? "the task's model"}. When you spawn an agent you can give it one of these models " +
               "with spawn_agent's model field (the id). If your goal or the user says which model to use for a kind of work, follow " +
               "that. Otherwise leave model out (the new agent runs on yours), or pick a cheaper model for routine work and a " +
               "stronger one for hard reasoning. A model's cost comes out of the budget you give the agent.\n" +
               string.Join("\n", lines);
    }

    private static string Price(decimal perMillion) =>
        perMillion == 0 ? "free" : "$" + perMillion.ToString("0.##", CultureInfo.InvariantCulture);
}
