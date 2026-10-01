using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AgentRuntime.Agents;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Durability;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure;
using AgentRuntime.LLM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Evals;

/// <summary>
/// The agent runtime in this process (an Orleans silo with in-memory storage, the configured LLM
/// provider, no database), started once per variant so each variant runs on its own configuration.
/// </summary>
public sealed class EvalHost : IAsyncDisposable
{
    private readonly IHost _host;

    private EvalHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static async Task<EvalHost> StartAsync(IDictionary<string, string?> settings, CancellationToken ct = default)
    {
        var siloPort = FreePort();
        var gatewayPort = FreePort();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tools:WorkspaceRoot"] = Path.Combine(Path.GetTempPath(), "aktor-evals", Guid.NewGuid().ToString("n")),
            // Evals measure behavior, not the network: keep agents inside the sandbox by default.
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Orleans"] = "Error"
        });
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddAgentRuntimeCore(builder.Configuration);
        builder.Services.AddAgentRuntimeStandalone(builder.Configuration);
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort, serviceId: "aktor-evals", clusterId: "evals-" + Guid.NewGuid().ToString("n")[..8]);
            silo.AddMemoryGrainStorage("Default");
            silo.UseInMemoryReminderService();
        });

        var host = builder.Build();
        await host.StartAsync(ct);
        return new EvalHost(host);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>
/// Runs every scenario of a spec N times under each variant and measures what matters about an
/// emergent team (roadmap P7): how big and deep it got, what it cost, how long it took, whether it
/// finished, and how good the answer was.
/// </summary>
public sealed class EvalRunner(Action<string>? log = null)
{
    /// <summary>Runtime settings from an appsettings.json, minus anything that reaches outside the
    /// process (connection strings, keys, URLs): an eval host never touches a database or a vault.</summary>
    public static Dictionary<string, string?> LoadSettings(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path), optional: false).Build();
        string[] keep = ["RuntimeLimits", "DefaultBudget", "TaskBudgetCeiling", "TeamPolicy", "Llm", "Supervision", "Prompts", "Preview"];
        return config.AsEnumerable()
            .Where(kv => kv.Value is not null && keep.Any(k => kv.Key.StartsWith(k + ":", StringComparison.Ordinal)) && !kv.Key.EndsWith("ApiKey", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async Task<EvalReport> RunAsync(EvalSpec spec, IDictionary<string, string?> baseSettings, CancellationToken ct = default)
    {
        var cells = new List<EvalCell>();
        var judgeName = "heuristic";
        foreach (var variant in spec.Variants)
        {
            var settings = new Dictionary<string, string?>(baseSettings);
            foreach (var (k, v) in variant.Settings) settings[k] = v;

            await using var host = await EvalHost.StartAsync(settings, ct);
            var llmOptions = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
            var judge = OutputJudge.Create(spec.Judge, host.Services);
            judgeName = judge.Name;
            var promptVersion = PromptVersion(host.Services);

            foreach (var scenario in spec.Scenarios)
            {
                log?.Invoke($"[{variant.Name}] {scenario.Name}: {scenario.Runs} run(s)");
                var results = new List<RunResult>();
                string? recordedTaskId = null;
                for (var i = 0; i < scenario.Runs; i++)
                {
                    ReplaySpec? replay = null;
                    if (variant.Mode == "replay" && recordedTaskId is not null)
                    {
                        replay = new ReplaySpec { SourceTaskId = recordedTaskId, Mode = ReplayMode.Full };
                    }

                    var result = await RunOnceAsync(host.Services, scenario, judge, replay, TimeSpan.FromSeconds(spec.RunTimeoutSeconds), ct);
                    recordedTaskId ??= result.TaskId;
                    results.Add(result);
                    log?.Invoke($"  run {i + 1}: {result.Outcome}, {result.TeamSize} agents, ${result.CostUsd:F4}, quality {result.Quality:F1}" +
                                (result.Reproduced is { } r ? $", reproduced={r}" : string.Empty));
                }

                var replays = results.Where(r => r.Reproduced is not null).ToList();
                cells.Add(new EvalCell
                {
                    Scenario = scenario.Name,
                    Variant = variant.Name,
                    Model = $"{llmOptions.Provider}:{llmOptions.Model}",
                    PromptVersion = promptVersion,
                    Runs = results.Count,
                    CompletionRate = results.Count == 0 ? 0 : Math.Round((double)results.Count(r => r.Completed) / results.Count, 4),
                    TeamSize = Stat.Of(results.Select(r => (double)r.TeamSize)),
                    Depth = Stat.Of(results.Select(r => (double)r.MaxDepth)),
                    Tokens = Stat.Of(results.Select(r => (double)r.Tokens)),
                    CostUsd = Stat.Of(results.Select(r => (double)r.CostUsd)),
                    DurationSeconds = Stat.Of(results.Select(r => r.DurationSeconds)),
                    Quality = Stat.Of(results.Select(r => r.Quality)),
                    ReplayFidelity = replays.Count == 0 ? null : Math.Round((double)replays.Count(r => r.Reproduced == true) / replays.Count, 4),
                    Results = results
                });
            }
        }

        return new EvalReport { Judge = judgeName, Cells = cells };
    }

    private static async Task<RunResult> RunOnceAsync(IServiceProvider services, EvalScenario scenario, OutputJudge judge, ReplaySpec? replay,
        TimeSpan timeout, CancellationToken ct)
    {
        var orchestrator = services.GetRequiredService<IAgentOrchestrator>();
        var events = services.GetRequiredService<IEventStream>();
        var taskId = "eval-" + Guid.NewGuid().ToString("n")[..12];

        // Agents report their result on their completion event; collect them as they come.
        var completions = new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>();
        var artifacts = new ConcurrentBag<string>();
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listener = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in events.Subscribe(listening.Token))
                {
                    if (e.TaskId != taskId) continue;
                    if (e.Type == RuntimeEventType.AgentCompleted && e.AgentId is not null) completions[e.AgentId] = e.Data;
                    if (e.Type == RuntimeEventType.ArtifactCreated) artifacts.Add(e.Data.GetValueOrDefault("location") ?? e.Summary);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        await Task.Delay(20, ct); // the subscription is live before the run starts

        var started = DateTimeOffset.UtcNow;
        string rootId;
        try
        {
            rootId = await orchestrator.CreateRootAgentAsync(taskId, scenario.Goal, new TaskLaunchOptions { Budget = scenario.Budget, Replay = replay }, ct);
        }
        catch (Exception ex)
        {
            await listening.CancelAsync();
            return new RunResult { TaskId = taskId, Outcome = "rejected", Error = ex.Message };
        }

        AgentSnapshot? root = null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            root = await orchestrator.GetSnapshotAsync(rootId, ct);
            if (root?.Status is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut) break;
            await Task.Delay(100, ct);
        }

        // Give the completion event a moment to arrive, then stop the team if it's still going.
        for (var i = 0; i < 20 && !completions.ContainsKey(rootId) && root?.Status == AgentStatus.Completed; i++) await Task.Delay(50, ct);
        var team = await orchestrator.FindAgentsAsync(new FindAgentsQuery { RootAgentId = rootId }, ct);
        var snapshots = (await Task.WhenAll(team.Select(a => orchestrator.GetSnapshotAsync(a.AgentId, ct)))).OfType<AgentSnapshot>().ToList();
        foreach (var a in snapshots.Where(s => !s.CompletedAt.HasValue && s.Status is not (AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut)))
        {
            await orchestrator.StopAsync(a.AgentId, ct);
        }

        await listening.CancelAsync();
        await listener;

        var rootData = completions.GetValueOrDefault(rootId);
        var outcome = root is null || root.Status is not (AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut)
            ? "timeout"
            : root.Status == AgentStatus.Completed ? rootData?.GetValueOrDefault("status") ?? "completed" : root.Status.ToString().ToLowerInvariant();
        var summary = rootData?.GetValueOrDefault("summary");
        var findings = completions.Where(c => c.Key != rootId).Select(c => c.Value.GetValueOrDefault("summary") ?? string.Empty).ToList();
        var verdict = await judge.ScoreAsync(scenario, summary, findings, artifacts.ToList(), ct);

        bool? reproduced = null;
        if (replay is not null)
        {
            var journal = services.GetRequiredService<IStepJournal>();
            var diff = RunDiff.Compare(await journal.ListAsync(replay.SourceTaskId, ct), await journal.ListAsync(taskId, ct));
            reproduced = diff.Steps.Where(s => s.Kind == JournalStep.LlmKind).All(s => s.Status == StepDiffStatus.Same) && diff.AgentsOnlyInA.Count == 0;
        }

        return new RunResult
        {
            TaskId = taskId,
            Completed = outcome == "completed",
            Outcome = outcome,
            TeamSize = snapshots.Count,
            MaxDepth = snapshots.Count == 0 ? 0 : snapshots.Max(s => s.Depth),
            Tokens = snapshots.Sum(s => (long)s.Usage.TokensUsed),
            CostUsd = Math.Round(snapshots.Sum(s => s.Usage.CostUsd), 6),
            DurationSeconds = Math.Round(((root?.CompletedAt ?? DateTimeOffset.UtcNow) - started).TotalSeconds, 2),
            Quality = verdict.Score,
            QualityRationale = verdict.Rationale,
            Summary = summary,
            Reproduced = reproduced
        };
    }

    /// <summary>
    /// Identifies the prompt the agents get: a hash of the stable part of a root agent's system
    /// prompt. Any change to the prompt sections (or to Prompts:ExtraInstructions) changes it, so
    /// reports compare like with like.
    /// </summary>
    public static string PromptVersion(IServiceProvider services)
    {
        var builder = services.GetRequiredService<IAgentPromptBuilder>();
        var prompt = builder.BuildSystemPrompt(new AgentPromptContext
        {
            State = new AgentState { AgentId = "root", Name = "Root", Role = "Root Agent", Goal = "(goal)", RootAgentId = "root" },
            AvailableTools = [],
            AutonomyLevel = AutonomyLevel.Autonomous,
            EnvironmentSummary = string.Empty
        });
        var text = prompt.Content ?? string.Empty;
        var stable = prompt.CacheablePrefixLength is { } n && n <= text.Length ? text[..n] : text;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable)))[..12].ToLowerInvariant();
    }
}
