namespace AgentRuntime.Contracts;

/// <summary>
/// Runtime-enforced permissions. An LLM cannot grant these to itself (CLAUDE.md section 18/44).
/// </summary>
[Flags]
public enum ToolPermission
{
    None = 0,
    ReadFilesystem = 1 << 0,
    WriteFilesystem = 1 << 1,
    ExecuteShell = 1 << 2,
    NetworkAccess = 1 << 3,
    GitRead = 1 << 4,
    GitWrite = 1 << 5,
    DatabaseRead = 1 << 6,
    DatabaseWrite = 1 << 7,
    SpawnAgents = 1 << 8,
    SendMessages = 1 << 9,

    // 1 << 10 was WorldActions (the world simulation, now on the product/studies branch) and
    // 1 << 11 WorkspaceActions (workspace agents' own triggers and notifications, before
    // pipelines). Both kept unused so stored permissions keep their meaning.

    /// <summary>Using tools provided by the workspace's connections (MCP servers, APIs, messaging).</summary>
    Integrations = 1 << 12
}
