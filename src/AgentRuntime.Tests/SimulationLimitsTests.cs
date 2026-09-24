using AgentRuntime.Configuration;
using AgentRuntime.Simulation;
using Xunit;

namespace AgentRuntime.Tests;

public class SimulationLimitsTests
{
    [Theory]
    [InlineData("Ollama", true)]
    [InlineData("ollama", true)]
    [InlineData("OpenAI", false)]
    [InlineData("Mock", false)]
    public void IsLocal_OnlyForOllama(string provider, bool expected) =>
        Assert.Equal(expected, new LlmOptions { Provider = provider }.IsLocal);

    [Fact]
    public void LocalModel_GetsFewerResidentsAndSlowerTicks()
    {
        var options = new SimulationOptions();
        var cloud = options.LimitsFor(localModel: false);
        var local = options.LimitsFor(localModel: true);

        Assert.True(local.DefaultPopulation < cloud.DefaultPopulation);
        Assert.True(local.MaxInitialPopulation < cloud.MaxInitialPopulation);
        Assert.True(local.MaxPopulation < cloud.MaxPopulation);
        Assert.True(local.DefaultTickIntervalSeconds > cloud.DefaultTickIntervalSeconds);
        Assert.True(local.MinTickIntervalSeconds > cloud.MinTickIntervalSeconds);
        // The default time limit must leave room for every default tick at the slower pace.
        Assert.True(local.DefaultMaxTicks * local.DefaultTickIntervalSeconds <= local.DefaultMaxDurationMinutes * 60);
    }
}
