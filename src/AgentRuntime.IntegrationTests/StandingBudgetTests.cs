using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Standing agents' own daily budgets: the coordinator isn't capped by one (only by the workspace's
/// budget), and any other standing agent that runs out says so in the chat instead of going quiet.
/// </summary>
public sealed class StandingBudgetTests : IAsyncLifetime
{
    private const int TokensPerCall = 1_000;
    private InProcessTestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        ScriptedLlmProviderRegistry.Current = Script;
        _cluster = await DurableTestCluster.StartAsync(new Dictionary<string, string?>
        {
            // A standing agent's own daily budget covers barely two calls; the workspace's is ample.
            ["Workspaces:StandingAgentDailyTokens"] = "1500"
        });
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(ToolCall call) =>
        new() { ToolCalls = [call], FinishReason = LlmFinishReason.ToolCalls, InputTokens = TokensPerCall };

    private LlmCompletionResponse Script(LlmCompletionRequest r)
    {
        _requests.Enqueue(r);
        var isCoordinator = r.Messages[0].Content?.Contains("acting as: Coordinator.") == true;
        var input = r.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        if (r.Messages[^1].Role == ChatRole.Tool) return Respond(Call("wait_for_events", new { summary = "Done for now." }));

        if (isCoordinator)
        {
            if (input.Contains("Your goal:"))
            {
                return Respond(Call("spawn_agent", new { role = "Stock Monitor", goal = "Check stock", standing = true, why_not_myself = "Ongoing monitoring." }));
            }

            return input.Contains("[Message from the user")
                ? Respond(Call("notify_user", new { text = "Answer: " + input.Split('\n').Last() }))
                : Respond(Call("wait_for_events", new { summary = "Idle." }));
        }

        // The monitor wakes itself every second, so it spends its own small budget quickly.
        return input.Contains("Your goal:")
            ? Respond(Call("create_schedule", new { name = "check", instruction = "Check stock.", every_minutes = 1.0 / 60 }))
            : Respond(Call("notify_user", new { text = "Stock checked." }));
    }

    private async Task<WorkspaceSnapshot> WaitForAsync(string id, Func<WorkspaceSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        WorkspaceSnapshot? s = null;
        while (DateTime.UtcNow < deadline)
        {
            s = await Workspace(id).GetSnapshot();
            if (s is not null && condition(s)) return s;
            await Task.Delay(200);
        }

        throw new TimeoutException("Workspace condition not met. Conversation:\n" +
                                   string.Join("\n", s?.Conversation.Select(c => $"[{c.AuthorName}] {c.Text}") ?? []));
    }

    [Fact]
    public async Task TheCoordinator_KeepsAnswering_PastAStandingAgentsDailyBudget()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Shop", Goal = "Watch my stock." });
        await WaitForAsync(id, s => s.Agents.Any(a => a.Role == "Stock Monitor"));

        // Each exchange costs the coordinator two calls: it's well past 1,500 tokens by the third.
        for (var i = 1; i <= 3; i++)
        {
            await Workspace(id).PostUserMessage($"question {i}", null, null);
            await WaitForAsync(id, s => s.Conversation.Any(c => c.Text == $"Answer: question {i}"));
        }

        var coordinator = (await WaitForAsync(id, _ => true)).Agents.Single(a => a.Role == "Coordinator");
        Assert.True(coordinator.TokensUsed > 1_500, $"coordinator used {coordinator.TokensUsed}");
    }

    [Fact]
    public async Task AStandingAgentOutOfItsOwnBudget_SaysSoInTheChat_Once()
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Shop", Goal = "Watch my stock." });

        var s = await WaitForAsync(id, s => s.Conversation.Any(c => c.Text.Contains("'Stock Monitor' has used its own daily token budget")));

        // It keeps being woken by its schedule, but the notice appears only once per pause.
        await Task.Delay(3000);
        s = await WaitForAsync(id, _ => true);
        Assert.Single(s.Conversation, c => c.Text.Contains("'Stock Monitor' has used its own daily"));
        Assert.Contains(s.Conversation, c => c.Text.Contains("The coordinator and other agents keep working"));
    }
}
