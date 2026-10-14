using System.Text.RegularExpressions;

namespace AgentRuntime.Workspaces;

/// <summary>
/// A workspace's variables (docs/workspaces.md): named values every stage of its pipeline can use,
/// set once instead of repeated in each stage. Actions and scripts read them as <c>vars.name</c>
/// (<c>{{ vars.email_recipient }}</c> in arguments); agent stages get <c>{{ vars.name }}</c> in their
/// instructions filled in, and see the whole list. They're plain settings, not secrets: agents and
/// run data see them, so keys and passwords belong in a connection.
/// </summary>
public static partial class WorkspaceVariables
{
    public const int MaxCount = 50;
    public const int MaxValueLength = 4000;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Every problem with <paramref name="variables"/>; empty when they can be saved.</summary>
    public static List<string> Validate(IReadOnlyDictionary<string, string> variables)
    {
        var errors = new List<string>();
        if (variables.Count > MaxCount) errors.Add($"A workspace can have at most {MaxCount} variables.");
        foreach (var (name, value) in variables)
        {
            if (!IsValidName(name)) errors.Add($"'{name}' isn't a valid variable name: lowercase letters, digits and underscores, starting with a letter (40 at most).");
            if ((value ?? string.Empty).Length > MaxValueLength) errors.Add($"'{name}' is longer than {MaxValueLength} characters.");
        }

        return errors;
    }
}
