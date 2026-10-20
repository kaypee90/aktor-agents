using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Global connectors (docs/plugins.md#global-connectors): set up once per organization, linked from
/// workspaces and tasks. A link uses the connector's secrets, read live (a rotation reaches every
/// link), keeps none of its own, can't be linked from another organization, and removing it leaves
/// the connector's secrets for everyone else.
/// </summary>
public sealed class GlobalConnectorTests : IAsyncLifetime
{
    private const string Tenant = "org-globals";
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        FakeCrmPlugin.Reset();
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.GrainFactory.GetGrain<IWorkspaceGrain>(id);

    private static async Task<GlobalConnector> CreateGlobalAsync(string name, string apiKey, bool usableInTasks = false, string tenant = Tenant)
    {
        var connector = new GlobalConnector
        {
            ConnectorId = GlobalConnectors.NewId(),
            TenantId = tenant,
            Name = name,
            PluginId = "fake-crm",
            Settings = new Dictionary<string, string> { ["region"] = "west-africa" },
            SecretKeys = ["api_key"],
            UsableInTasks = usableInTasks,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        InMemorySecretStore.Values[(GlobalConnectors.Scope(connector.ConnectorId), "api_key")] = apiKey;
        await TestGlobalConnectors.Store.SaveAsync(connector);
        return connector;
    }

    private async Task<string> CreateWorkspaceAsync(string tenant = Tenant)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { TenantId = tenant, Name = "Shop", Goal = "Run my shop." });
        return id;
    }

    /// <summary>A watch's dry run calls crm__list_inventory, and the fake CRM records the key it got.</summary>
    private async Task<string> KeyUsedByAsync(string workspaceId, string connection = "crm")
    {
        FakeCrmPlugin.ToolCalls.Clear();
        var added = await Workspace(workspaceId).AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Watch,
            Name = $"Stock {Guid.NewGuid():n}"[..14],
            SourceTool = $"{connection}__list_inventory",
            Rule = new WatchRule { ItemsPath = "$.body.items[*]", Conditions = [new WatchCondition { Field = "qty", Op = "<", Value = "1" }], KeyField = "sku" },
            EveryMinutes = 60
        }, "test", Guid.NewGuid().ToString("n"), revealSecret: false);
        Assert.True(added.Success, added.Message);
        return Assert.Single(FakeCrmPlugin.ToolCalls).ApiKey;
    }

    [Fact]
    public async Task Workspaces_link_a_global_connector_and_use_its_secrets_live()
    {
        var global = await CreateGlobalAsync("shared_crm", "key-one");
        var first = await CreateWorkspaceAsync();
        var second = await CreateWorkspaceAsync();

        foreach (var ws in new[] { first, second })
        {
            var linked = await Workspace(ws).AddConnection(new ConnectionRequest { PluginId = string.Empty, Name = "crm", GlobalConnector = "shared_crm" });
            Assert.True(linked.Success, linked.Message);
            Assert.Equal(global.ConnectorId, linked.Connection!.GlobalConnectorId);
            Assert.Equal("shared_crm", linked.Connection.GlobalConnectorName);
            Assert.Equal("fake-crm", linked.Connection.PluginId);
            Assert.Contains(linked.Connection.Tools, t => t.Name == "crm__list_inventory");
        }

        // Nothing was copied into the workspaces' own vault scopes.
        Assert.DoesNotContain(InMemorySecretStore.Values.Keys, k => k.Scope.StartsWith(first) || k.Scope.StartsWith(second));
        Assert.Equal(2, (await TestGlobalConnectors.Store.LinksAsync(Tenant, global.ConnectorId)).Count);

        Assert.Equal("key-one", await KeyUsedByAsync(first));
        // Rotated once, used everywhere.
        InMemorySecretStore.Values[(GlobalConnectors.Scope(global.ConnectorId), "api_key")] = "key-two";
        Assert.Equal("key-two", await KeyUsedByAsync(first));
        Assert.Equal("key-two", await KeyUsedByAsync(second));

        // Its settings are the connector's: a workspace can't change them on its own.
        var connection = (await Workspace(first).ListConnections()).Single();
        var edit = await Workspace(first).UpdateConnection(connection.ConnectionId, new ConnectionUpdate { Settings = new() { ["region"] = "eu" } });
        Assert.False(edit.Success);
        Assert.Contains("global connector", edit.Message);

        // Removing one link keeps the connector's secrets for the other.
        await Workspace(first).RemoveConnection(connection.ConnectionId);
        Assert.Equal("key-two", InMemorySecretStore.Values[(GlobalConnectors.Scope(global.ConnectorId), "api_key")]);
        Assert.Single(await TestGlobalConnectors.Store.LinksAsync(Tenant, global.ConnectorId));
        Assert.Equal("key-two", await KeyUsedByAsync(second));
    }

    [Fact]
    public async Task Another_organization_cant_link_it()
    {
        await CreateGlobalAsync("private_crm", "key");
        var theirs = await CreateWorkspaceAsync("org-someone-else");
        var linked = await Workspace(theirs).AddConnection(new ConnectionRequest { PluginId = string.Empty, Name = "crm", GlobalConnector = "private_crm" });
        Assert.False(linked.Success);
        Assert.Contains("no global connector", linked.Message);
    }

    [Fact]
    public async Task Tasks_link_it_only_when_an_admin_allowed_it()
    {
        await CreateGlobalAsync("closed_crm", "key", usableInTasks: false);
        var open = await CreateGlobalAsync("open_crm", "key-open", usableInTasks: true);
        var task = _cluster.GrainFactory.GetGrain<ITaskConnectionsGrain>(Guid.NewGuid().ToString("n"));

        var refused = await task.AddConnection(Tenant, new ConnectionRequest { PluginId = string.Empty, Name = "crm", GlobalConnector = "closed_crm" });
        Assert.False(refused.Success);
        Assert.Contains("isn't available to tasks", refused.Message);

        // A solution's assistant: chosen by an admin at deploy, so it may link a closed one.
        var chosen = await task.AddConnection(Tenant, new ConnectionRequest { PluginId = string.Empty, Name = "crm2", GlobalConnector = "closed_crm", AdminChosenGlobal = true });
        Assert.True(chosen.Success, chosen.Message);

        var linked = await task.AddConnection(Tenant, new ConnectionRequest { PluginId = string.Empty, Name = "crm", GlobalConnector = "open_crm" });
        Assert.True(linked.Success, linked.Message);
        Assert.Equal(open.ConnectorId, linked.Connection!.GlobalConnectorId);
        await task.RemoveConnection(linked.Connection.ConnectionId);
        Assert.Equal("key-open", InMemorySecretStore.Values[(GlobalConnectors.Scope(open.ConnectorId), "api_key")]);
    }

    [Fact]
    public async Task A_bad_key_fails_the_link_without_touching_the_connector()
    {
        var global = await CreateGlobalAsync("broken_crm", "bad");
        var ws = await CreateWorkspaceAsync();
        var linked = await Workspace(ws).AddConnection(new ConnectionRequest { PluginId = string.Empty, Name = "crm", GlobalConnector = "broken_crm" });
        Assert.False(linked.Success);
        Assert.Equal("bad", InMemorySecretStore.Values[(GlobalConnectors.Scope(global.ConnectorId), "api_key")]);
        Assert.Empty(await TestGlobalConnectors.Store.LinksAsync(Tenant, global.ConnectorId));
    }
}
