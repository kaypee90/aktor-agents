using AgentRuntime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRuntime.Tools;

/// <summary>
/// Central catalog of every tool implementation known to the runtime. Per-agent visibility is a
/// separate concern (see <see cref="AgentToolCatalog"/>) — this registry never lets an agent grant
/// itself a tool or permission it wasn't given (CLAUDE.md section 18).
/// </summary>
public sealed class ToolRegistry(IServiceProvider serviceProvider)
{
    // Resolved lazily rather than taking IEnumerable<ITool> in the constructor: some tools (e.g.
    // SpawnAgentTool) depend on IAgentOrchestrator, which itself depends on this registry. Eagerly
    // resolving ITool instances during ToolRegistry construction would be a circular dependency.
    private Dictionary<string, ITool>? _tools;

    private Dictionary<string, ITool> Tools => _tools ??=
        serviceProvider.GetServices<ITool>().ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ToolDefinition> AllDefinitions => Tools.Values.Select(t => t.Definition).ToList();

    public bool TryGet(string name, out ITool tool) => Tools.TryGetValue(name, out tool!);

    /// <summary>
    /// Executes a tool call after verifying the calling agent is both allowed to see the tool and
    /// holds the permissions it requires. This is the runtime authority described in section 50 —
    /// the LLM decided to call the tool, but the runtime decides whether that's allowed.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        IReadOnlyCollection<string> agentAllowedTools,
        ToolPermission agentGrantedPermissions)
    {
        if (!Tools.TryGetValue(request.ToolName, out var tool))
        {
            return ToolExecutionResult.Fail($"Unknown tool '{request.ToolName}'.");
        }

        if (!agentAllowedTools.Contains(request.ToolName, StringComparer.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Fail(
                $"Agent '{request.AgentId}' does not have '{request.ToolName}' in its allowed tool set.");
        }

        var required = tool.Definition.RequiredPermissions;
        if ((agentGrantedPermissions & required) != required)
        {
            return ToolExecutionResult.Fail(
                $"Agent '{request.AgentId}' lacks required permissions for '{request.ToolName}' " +
                $"(needs {required}, has {agentGrantedPermissions}).");
        }

        try
        {
            return await tool.ExecuteAsync(request with { GrantedPermissions = agentGrantedPermissions });
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Tool '{request.ToolName}' threw: {ex.Message}");
        }
    }
}

/// <summary>
/// Maps agent capabilities to the default set of tool names an agent of that capability profile
/// should be granted, per CLAUDE.md section 17's example (SecurityAgent vs DatabaseAgent, etc).
/// Always-available governance tools are separate from this and granted to every agent.
/// </summary>
public static class AgentToolCatalog
{
    public static readonly string[] GovernanceTools =
    [
        "spawn_agent", "find_agents", "send_message", "get_agent_status",
        "list_children", "read_memory", "write_memory", "complete_task"
    ];

    /// <summary>Tools confined to the task's own sandboxed workspace. Children inherit these from
    /// their parent regardless of capability keywords: a model naming a child "Python Developer"
    /// rather than "filesystem" shouldn't leave it unable to write the code it was asked for.</summary>
    public static readonly string[] WorkspaceTools = ["filesystem_read", "filesystem_write", "filesystem_list"];

    private static readonly Dictionary<string, string[]> CapabilityToolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web-search"] = ["web_search"],
        ["research"] = ["web_search", "search_knowledge"],
        ["postgresql"] = ["database_query"],
        ["database-design"] = ["database_query"],
        ["filesystem"] = ["filesystem_read", "filesystem_write", "filesystem_list"],
        ["shell"] = ["shell_exec"],
        ["docker"] = ["shell_exec"],
        ["http"] = ["http_request"],
        ["security-scanner"] = ["shell_exec", "http_request"]
    };

    public static IReadOnlyList<string> ResolveToolsForCapabilities(IEnumerable<string> capabilities)
    {
        var tools = new HashSet<string>(GovernanceTools, StringComparer.OrdinalIgnoreCase)
        {
            "search_knowledge"
        };

        foreach (var capability in capabilities)
        {
            if (CapabilityToolMap.TryGetValue(capability, out var mapped))
            {
                foreach (var t in mapped) tools.Add(t);
            }
        }

        return tools.ToList();
    }
}
