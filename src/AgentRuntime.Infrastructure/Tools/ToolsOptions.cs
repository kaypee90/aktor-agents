namespace AgentRuntime.Infrastructure.Tools;

/// <summary>Configuration for infrastructure-bound tools (CLAUDE.md section 45).</summary>
public sealed class ToolsOptions
{
    public const string SectionName = "Tools";

    /// <summary>Root directory all filesystem/shell tool activity is sandboxed under.</summary>
    public string WorkspaceRoot { get; set; } = "./workspace";

    /// <summary>Docker image used to run shell_exec in isolation (CLAUDE.md section 19).</summary>
    public string ShellDockerImage { get; set; } = "alpine:3.20";

    public int ShellTimeoutSeconds { get; set; } = 30;
    public string ShellMemoryLimit { get; set; } = "256m";
    public string ShellCpuLimit { get; set; } = "0.5";

    /// <summary>Which search API web_search calls: "Tavily" (free tier, no card) or "Brave".</summary>
    public string SearchProvider { get; set; } = "Tavily";

    /// <summary>API key for <see cref="SearchProvider"/>. Left empty, web_search reports unavailable.</summary>
    public string? SearchApiKey { get; set; }

    /// <summary>Optional endpoint override; defaults to the selected provider's public endpoint.</summary>
    public string? SearchApiUrl { get; set; }

    public int SearchMaxResults { get; set; } = 5;

    public int HttpTimeoutSeconds { get; set; } = 15;
    public int HttpMaxResponseBytes { get; set; } = 200_000;

    /// <summary>Connection string agents' database_query tool runs against. The runtime holds this
    /// credential — it is never handed to the agent (CLAUDE.md section 44).</summary>
    public string? DatabaseConnectionString { get; set; }

    /// <summary>
    /// Non-superuser role that database_query actually runs as. At startup the runtime uses
    /// <see cref="DatabaseConnectionString"/> (an admin connection) only to provision this role and
    /// its schema; agent SQL never executes with admin rights, so Postgres itself — not a blocklist —
    /// keeps agents out of the runtime's history tables and server-side functions like pg_read_file.
    /// </summary>
    public string DatabaseAgentRole { get; set; } = "aktor_agent";
    public string DatabaseAgentSchema { get; set; } = "agent_scratch";

    /// <summary>Leave empty to have a random password generated at each startup.</summary>
    public string? DatabaseAgentPassword { get; set; }

    public int DatabaseStatementTimeoutSeconds { get; set; } = 10;
}
