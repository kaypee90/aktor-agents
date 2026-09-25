using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRuntime.Tests;

public class PluginLoaderTests
{
    [Fact]
    public void MissingDirectory_LoadsNothing() =>
        Assert.Empty(new ServiceCollection().AddPluginsFromDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));

    /// <summary>Loads the sample plugin (samples/ExamplePlugin) the way the server does. Skipped
    /// when the sample hasn't been built: dotnet build samples/ExamplePlugin -c Release.</summary>
    [Fact]
    public void SamplePluginDll_IsDiscoveredAndConstructedWithDependencyInjection()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "AktorAgents.slnx"))) root = Path.GetDirectoryName(root);
        var dll = root is null ? null : Path.Combine(root, "samples", "ExamplePlugin", "bin", "Release", "net10.0", "ExamplePlugin.dll");
        if (dll is null || !File.Exists(dll)) return;

        var dir = Directory.CreateTempSubdirectory("aktor-plugins-");
        File.Copy(dll, Path.Combine(dir.FullName, "ExamplePlugin.dll"));
        var services = new ServiceCollection().AddHttpClient();

        var loaded = services.AddPluginsFromDirectory(dir.FullName);
        using var provider = services.BuildServiceProvider();
        var plugin = Assert.Single(provider.GetServices<IAgentPlugin>());

        Assert.Single(loaded);
        Assert.Equal("example-weather", plugin.Manifest.Id);
        Assert.IsAssignableFrom<IToolProviderPlugin>(plugin);
    }
}
