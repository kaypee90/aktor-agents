using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// The per-organization SQL sandbox against a real Postgres. Skipped unless SANDBOX_TEST_PG holds an
/// admin connection string to a throwaway database.
/// </summary>
public sealed class SandboxLiveTests
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable("SANDBOX_TEST_PG");

    private static ToolExecutionRequest Sql(string tenant, string sql, bool write = true) => new()
    {
        ToolName = "database_query",
        AgentId = "agent-" + tenant,
        TaskId = "task-" + tenant,
        TenantId = tenant,
        ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { sql }),
        GrantedPermissions = write ? ToolPermission.DatabaseRead | ToolPermission.DatabaseWrite : ToolPermission.DatabaseRead
    };

    [Fact]
    public async Task EachOrganization_HasItsOwnScratchSchema()
    {
        if (string.IsNullOrEmpty(Admin)) return;

        var sandbox = new AgentDatabaseSandbox(Options.Create(new ToolsOptions { DatabaseConnectionString = Admin }), NullLogger<AgentDatabaseSandbox>.Instance);
        await sandbox.ProvisionAsync();
        var tool = new DatabaseQueryTool(sandbox);
        var a = "t-" + Guid.NewGuid().ToString("n")[..10];
        var b = "t-" + Guid.NewGuid().ToString("n")[..10];

        Assert.True((await tool.ExecuteAsync(Sql(a, "CREATE TABLE customers (name text)"))).Success);
        Assert.True((await tool.ExecuteAsync(Sql(a, "INSERT INTO customers VALUES ('Ama')"))).Success);
        var own = await tool.ExecuteAsync(Sql(a, "SELECT name FROM customers", write: false));
        Assert.True(own.Success, own.ErrorMessage);
        Assert.Contains("Ama", own.ResultJson);

        // Another organization: same name resolves to nothing, and the other schema is off limits.
        var other = await tool.ExecuteAsync(Sql(b, "SELECT name FROM customers", write: false));
        Assert.False(other.Success);
        var schemaA = $"agent_scratch_{AgentDatabaseSandbox.TenantSuffix(a)}";
        var qualified = await tool.ExecuteAsync(Sql(b, $"SELECT name FROM {schemaA}.customers", write: false));
        Assert.False(qualified.Success);
        Assert.Contains("permission denied", qualified.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The default organization's role can't reach it either, nor the runtime's own tables.
        Assert.False((await tool.ExecuteAsync(Sql("default", $"SELECT name FROM {schemaA}.customers", write: false))).Success);
        Assert.False((await tool.ExecuteAsync(Sql(b, "SELECT * FROM public.\"Users\"", write: false))).Success);
    }
}
