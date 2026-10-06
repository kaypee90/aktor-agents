using System.Diagnostics;
using AgentRuntime.Configuration;
using AgentRuntime.Infrastructure.Llm;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Options;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Measures how many helpers a real model starts in a pipeline stage for requests of different
/// sizes, and whether the run finishes. Unlike the scripted tests this judges the model's decisions, so it only
/// runs when pointed at a model, and never in CI:
/// <code>
///   EVAL_LLM_PROVIDER=Ollama EVAL_LLM_MODEL=qwen3:8b \
///     dotnet test src/AgentRuntime.IntegrationTests --filter SpawnEfficiencyEval --logger "console;verbosity=detailed"
/// </code>
/// Also EVAL_LLM_BASE_URL, EVAL_LLM_API_KEY (Anthropic, OpenAI) and EVAL_CASE_TIMEOUT_MINUTES
/// (default 15). It prints one row per request and fails listing any request that spawned more
/// agents than it needed or didn't finish cleanly.
/// </summary>
public sealed class SpawnEfficiencyEval(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? Provider = Environment.GetEnvironmentVariable("EVAL_LLM_PROVIDER");
    private static readonly string? Model = Environment.GetEnvironmentVariable("EVAL_LLM_MODEL");

    /// <summary>A request and the most agents it should need.</summary>
    private sealed record EvalCase(string Kind, string Request, int MaxAgents);

    private static readonly EvalCase[] Cases =
    [
        new("simple", "Suggest three names for a new line of scented candles.", 0),
        new("simple", "Explain in two sentences what a webhook is.", 0),
        new("simple", "Write a short thank-you note to a customer who left a five-star review.", 0),
        new("complex", "Write a launch plan for a new candle line with three separate parts: a pricing analysis, " +
                       "a social media calendar for four weeks, and an email announcement sequence. Save each part as a file.", 3)
    ];

    private InProcessTestCluster? _cluster;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Provider) || string.IsNullOrEmpty(Model)) return;

        var llm = CreateProvider();
        ScriptedLlmProviderRegistry.CurrentAsync = (request, ct) => llm.CompleteAsync(request, ct);

        var settings = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = Provider,
            ["Llm:Model"] = Model,
            // The real funding minimum, not the scripted tests' zero.
            ["RuntimeLimits:MinChildTokens"] = "20000",
            ["RuntimeLimits:MinChildToolCalls"] = "5"
        };
        if (Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            settings["Llm:PricePerInputTokenUsd"] = "0";
            settings["Llm:PricePerOutputTokenUsd"] = "0";
        }

        _cluster = await DurableTestCluster.StartAsync(settings);
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.CurrentAsync = null;
        if (_cluster is not null) await _cluster.DisposeAsync();
    }

    private static ILLMProvider CreateProvider()
    {
        var baseUrl = Environment.GetEnvironmentVariable("EVAL_LLM_BASE_URL");
        var options = Options.Create(new LlmOptions
        {
            Provider = Provider!,
            Model = Model!,
            ApiKey = Environment.GetEnvironmentVariable("EVAL_LLM_API_KEY"),
            BaseUrl = baseUrl
        });

        HttpClient Client(string fallback)
        {
            var url = string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl;
            return new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"), Timeout = TimeSpan.FromMinutes(5) };
        }

        return Provider!.ToLowerInvariant() switch
        {
            "ollama" => new OllamaProvider(Client("http://localhost:11434/"), options),
            "anthropic" => new AnthropicProvider(Client("https://api.anthropic.com/"), options),
            "openai" => new OpenAIProvider(Client("https://api.openai.com/"), options),
            _ => throw new InvalidOperationException($"EVAL_LLM_PROVIDER '{Provider}' isn't supported here (Ollama, Anthropic, OpenAI).")
        };
    }

    [Fact]
    public async Task RequestsOfEachSize_SpawnOnlyTheAgentsTheyNeed_AndFinish()
    {
        if (_cluster is null) return; // not configured: nothing to evaluate against

        var timeout = TimeSpan.FromMinutes(double.TryParse(Environment.GetEnvironmentVariable("EVAL_CASE_TIMEOUT_MINUTES"), out var m) ? m : 15);
        var problems = new List<string>();
        output.WriteLine($"Model: {Provider} {Model}");
        output.WriteLine($"{"kind",-8} {"agents",6} {"max",4} {"tokens",9} {"secs",6}  outcome / roles");

        foreach (var c in Cases)
        {
            var result = await RunCaseAsync(c, timeout);
            output.WriteLine($"{c.Kind,-8} {result.Agents.Count,6} {c.MaxAgents,4} {result.Tokens,9:N0} {result.Seconds,6:N0}  " +
                             $"{result.Outcome}{(result.Agents.Count > 0 ? " / " + string.Join(", ", result.Agents) : string.Empty)}");

            if (result.Agents.Count > c.MaxAgents) problems.Add($"'{c.Request}' spawned {result.Agents.Count} agents (at most {c.MaxAgents} needed): {string.Join(", ", result.Agents)}");
            if (result.Outcome != "finished") problems.Add($"'{c.Request}' {result.Outcome}");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private sealed record CaseResult(List<string> Agents, long Tokens, double Seconds, string Outcome);

    /// <summary>Runs one request through a fresh workspace's one-stage pipeline (the stage may start
    /// up to three helpers) and reports the helpers it started.</summary>
    private async Task<CaseResult> RunCaseAsync(EvalCase c, TimeSpan timeout)
    {
        var id = WorkspaceIds.New();
        var ws = _cluster!.Client.GetGrain<IWorkspaceGrain>(id);
        await ws.Create(new WorkspaceCreationRequest
        {
            Name = "Candle shop",
            Goal = "Help me run my small online candle shop.",
            Pipeline = new Pipelines.PipelineDefinition
            {
                Stages =
                [
                    new Pipelines.PipelineStage
                    {
                        StageId = "assistant",
                        Name = "Assistant",
                        Instructions = "Do what the run's input asks, for the candle shop.",
                        Capabilities = ["research", "filesystem"],
                        MaxHelpers = 3
                    }
                ]
            }
        });

        var clock = Stopwatch.StartNew();
        var started = await ws.StartRun(c.Request, "eval");
        var run = _cluster.Client.GetGrain<Pipelines.IPipelineRunGrain>(started.RunId!);
        Pipelines.PipelineRunView? view = null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            view = await run.GetView();
            if (view?.Status is not (null or Pipelines.PipelineRunStatus.Queued or Pipelines.PipelineRunStatus.Running)) break;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        var team = await _cluster.Client.GetGrain<Agents.IAgentRegistryGrain>(0).FindAsync(new Contracts.FindAgentsQuery { RootAgentId = started.RunId });
        var helpers = team.Where(a => a.Depth >= 2).ToList();
        var snapshot = await ws.GetSnapshot();
        var outcome = view?.Status switch
        {
            Pipelines.PipelineRunStatus.Completed => "finished",
            null or Pipelines.PipelineRunStatus.Queued or Pipelines.PipelineRunStatus.Running => "timed out",
            var other => other.ToString()!.ToLowerInvariant()
        };
        return new(helpers.Select(a => $"{a.Role} ({a.Status})").ToList(), snapshot?.TotalTokens ?? 0, clock.Elapsed.TotalSeconds, outcome);
    }
}
