using AgentRuntime.Evals;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Roadmap P7 in CI: both demo scenarios run through the eval harness on the Mock provider and are
/// compared with the committed baseline (evals/baseline/report.json). A change that makes teams
/// bigger, runs costlier, completion rarer, answers worse or replays less faithful fails here.
/// </summary>
public sealed class EvalHarnessTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AktorAgents.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public async Task Demo_scenarios_match_the_baseline_on_the_mock_provider()
    {
        var root = RepoRoot();
        var spec = EvalSpec.Load(Path.Combine(root, "evals", "demo-scenarios.json"));
        spec = spec with { Scenarios = spec.Scenarios.Select(s => s with { Runs = 2 }).ToList(), RunTimeoutSeconds = 120 };

        var settings = EvalRunner.LoadSettings(Path.Combine(root, "src", "AgentRuntime.Api", "appsettings.json"));
        settings["Llm:Provider"] = "Mock";
        var report = await new EvalRunner(output.WriteLine).RunAsync(spec, settings);
        output.WriteLine(EvalReports.Markdown(report));

        Assert.Equal(spec.Scenarios.Count * spec.Variants.Count, report.Cells.Count);
        Assert.All(report.Cells, c =>
        {
            Assert.Equal(1.0, c.CompletionRate);
            Assert.True(c.TeamSize.Mean > 1, $"{c.Scenario}/{c.Variant}: the team never formed");
            Assert.True(c.Quality.Mean > 0);
        });
        Assert.All(report.Cells.Where(c => c.Variant == "replay"), c => Assert.Equal(1.0, c.ReplayFidelity));
        Assert.NotEqual(report.Cells.First(c => c.Variant == "default").PromptVersion, report.Cells.First(c => c.Variant == "lean-teams").PromptVersion);

        var regressions = EvalReports.Compare(EvalReports.Load(Path.Combine(root, "evals", "baseline", "report.json")), report);
        Assert.True(regressions.Count == 0, string.Join("\n", regressions.Select(r => $"{r.Scenario}/{r.Variant} {r.Metric}: {r.Message}")));
    }
}
