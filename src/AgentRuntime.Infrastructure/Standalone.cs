using System.Collections.Concurrent;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Integrations;
using AgentRuntime.Memory;
using AgentRuntime.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRuntime.Infrastructure;

/// <summary>
/// The runtime without Postgres: the configured LLM provider, sandboxed file tools and web search,
/// with memory and secrets kept in process. For tools that host the runtime themselves, such as the
/// eval harness; nothing survives the process.
/// </summary>
public static class StandaloneRuntime
{
    public static IServiceCollection AddAgentRuntimeStandalone(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));
        services.AddHttpClient("agent-tools").ConfigurePrimaryHttpMessageHandler(PublicNetworkHandler.Create);
        services.AddSingleton<IMemoryStore, InMemoryMemoryStore>();
        services.AddSingleton<ISecretStore, InMemorySecretStore>();
        ServiceCollectionExtensions.AddLlmProvider(services, configuration);
        services.AddSingleton<ITool, WebSearchTool>();
        services.AddSingleton<ITool, FilesystemReadTool>();
        services.AddSingleton<ITool, FilesystemWriteTool>();
        services.AddSingleton<ITool, CreateDocumentTool>();
        services.AddSingleton<ITool, FilesystemListTool>();
        return services;
    }
}

/// <summary>Memory kept in process, scoped like the Postgres store (keyword search only).</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<string, MemoryRecord> _records = new();

    public Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.TenantId + "|" + record.AgentId + "|" + record.Key + "|" + record.WorkspaceId] = record;
        return Task.CompletedTask;
    }

    public Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, MemoryScope? scope = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values
            .Where(r => r.TenantId == tenantId && r.Key == key &&
                        (r.AgentId == agentId || (r.IsShared && (scope ?? MemoryScope.Organization).Includes(r.WorkspaceId))))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(string tenantId, string query, MemoryKind? kind = null, string? agentId = null,
        MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        var visible = scope ?? MemoryScope.Organization;
        IEnumerable<MemoryRecord> results = _records.Values.Where(r => r.TenantId == tenantId && visible.Includes(r.WorkspaceId));
        if (kind is { } k) results = results.Where(r => r.Kind == k);
        if (agentId is not null) results = results.Where(r => r.AgentId == agentId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            results = results.Where(r => r.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results.OrderByDescending(r => r.CreatedAt).ToList());
    }
}

/// <summary>Secrets kept in process (no connections are made by standalone hosts).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<(string Scope, string Key), string> _values = new();

    public Task PutAsync(string scope, string key, string value, CancellationToken cancellationToken = default)
    {
        _values[(scope, key)] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue((scope, key), out var v) ? v : null);

    public Task DeleteScopeAsync(string scope, CancellationToken cancellationToken = default)
    {
        foreach (var k in _values.Keys.Where(k => k.Scope == scope)) _values.TryRemove(k, out _);
        return Task.CompletedTask;
    }
}
