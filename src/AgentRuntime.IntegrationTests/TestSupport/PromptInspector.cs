namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>Pulls just enough out of the rendered system prompt for test scripts to branch on,
/// without the test needing to know the prompt's internal layout beyond these two markers.</summary>
public static class PromptInspector
{
    public static string ExtractRole(string systemText)
    {
        const string marker = "acting as: ";
        var i = systemText.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        i += marker.Length;
        var j = systemText.IndexOf('.', i);
        return j < 0 ? systemText[i..] : systemText[i..j];
    }

    public static string ExtractAgentName(string systemText)
    {
        const string marker = "You are '";
        var i = systemText.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        i += marker.Length;
        var j = systemText.IndexOf('\'', i);
        return j < 0 ? string.Empty : systemText[i..j];
    }
}
