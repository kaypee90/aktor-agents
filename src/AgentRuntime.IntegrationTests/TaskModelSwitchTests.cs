using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// A task's model (docs/llm-settings.md): agents call the model profile the task was started with,
/// and after a switch mid-run every agent uses the new one from its next step, including agents
/// spawned after the switch. Work done before the switch is kept.
/// </summary>
public sealed class TaskModelSwitchTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.StopAllSilosAsync();
        _cluster.Dispose();
    }

    private static ToolCall Call(string name, object args) => new()
    {
        Id = "call_" + Guid.NewGuid().ToString("n")[..8],
        Name = name,
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    private static LlmCompletionResponse Respond(ToolCall call) => new() { ToolCalls = [call], FinishReason = LlmFinishReason.ToolCalls };

    [Fact]
    public async Task Switching_mid_run_moves_every_agent_including_later_ones_to_the_new_model()
    {
        var tenant = "org-" + Guid.NewGuid().ToString("n")[..6];
        var taskId = Guid.NewGuid().ToString("n");
        await TestModels.Settings.SaveProfileAsync(tenant, new ModelProfile { Id = "careful", Name = "Careful", Provider = "Mock", Model = "careful-model" }, null, makeDefault: false);
        await TestModels.Settings.SaveProfileAsync(tenant, new ModelProfile { Id = "cheap", Name = "Cheap", Provider = "Mock", Model = "cheap-model" }, null, makeDefault: false);
        await TestModels.Selection.SetAsync(taskId, "careful");

        var calls = new ConcurrentQueue<(string Role, string? Profile, string? Model)>();
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!);
            calls.Enqueue((role, r.ModelProfileId, r.Model));
            if (!role.Contains("Root", StringComparison.OrdinalIgnoreCase)) return Respond(Call("complete_task", new { status = "completed", summary = "Checked." }));

            var spawned = r.Messages.Any(m => m.ToolCalls?.Any(t => t.Name == "spawn_agent") == true);
            if (spawned) return Respond(Call("complete_task", new { status = "completed", summary = "Done with help." }));

            // The user switches the task to the cheaper model while the root is still planning.
            TestModels.Selection.SetAsync(taskId, "cheap").GetAwaiter().GetResult();
            return Respond(Call("spawn_agent", new { role = "Checker", goal = "Check the numbers", why_not_myself = "Needs a separate pass in parallel." }));
        };

        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);
        await registry.RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = tenant });
        var root = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await root.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "Prepare the quarterly numbers.",
            AllowedTools = ["spawn_agent", "complete_task"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = new ResourceBudget(),
            TaskId = taskId,
            TenantId = tenant,
            AutoStart = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await root.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);
        Assert.Equal(AgentStatus.Completed, (await root.GetSnapshot()).Status);

        var all = calls.ToList();
        // The root's first step ran on the model the task started with...
        Assert.Equal(("careful", "careful-model"), (all[0].Profile, all[0].Model));
        // ...and every later step, the root's and the new agent's, on the one it was switched to.
        Assert.All(all.Skip(1), c => Assert.Equal(("cheap", "cheap-model"), (c.Profile, c.Model)));
        Assert.Contains(all, c => c.Role.Contains("Checker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_a_choice_tasks_use_the_organizations_default_or_the_server()
    {
        var withDefault = "org-" + Guid.NewGuid().ToString("n")[..6];
        var without = "org-" + Guid.NewGuid().ToString("n")[..6];
        await TestModels.Settings.SaveProfileAsync(withDefault, new ModelProfile { Id = "house", Name = "House model", Provider = "Mock", Model = "house-model" }, null, makeDefault: true);

        var seen = new ConcurrentDictionary<string, (string? Profile, string? Model)>();
        ScriptedLlmProviderRegistry.Current = r =>
        {
            seen[r.TenantId ?? ""] = (r.ModelProfileId, r.Model);
            return Respond(Call("complete_task", new { status = "completed", summary = "ok" }));
        };

        foreach (var tenant in new[] { withDefault, without })
        {
            var rootId = $"root-{Guid.NewGuid():n}"[..14];
            await _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0).RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = tenant });
            var grain = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
            await grain.Initialize(new AgentInitializationRequest
            {
                AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "g",
                AllowedTools = ["complete_task"], Budget = new ResourceBudget(), TaskId = Guid.NewGuid().ToString("n"), TenantId = tenant, AutoStart = true
            });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && (await grain.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);
        }

        Assert.Equal(("house", "house-model"), seen[withDefault]);
        Assert.Equal((ModelProfiles.ServerId, new Configuration.LlmOptions().Model), seen[without]);
    }

    private async Task<IAgentGrain> StartRootAsync(string tenant, string taskId, string goal)
    {
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0).RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = goal, RootAgentId = rootId, TenantId = tenant });
        var root = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await root.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = goal,
            AllowedTools = ["spawn_agent", "complete_task"],
            GrantedPermissions = ToolPermission.SpawnAgents | ToolPermission.SendMessages,
            Budget = new ResourceBudget(),
            TaskId = taskId,
            TenantId = tenant,
            AutoStart = true
        });
        return root;
    }

    [Fact]
    public async Task Agents_give_spawned_agents_the_model_the_goal_names_and_unknown_models_are_refused()
    {
        var tenant = "org-" + Guid.NewGuid().ToString("n")[..6];
        var taskId = Guid.NewGuid().ToString("n");
        await TestModels.Settings.SaveProfileAsync(tenant, new ModelProfile { Id = "careful", Name = "Careful", Description = "hard analysis", Provider = "Mock", Model = "careful-model" }, null, makeDefault: true);
        await TestModels.Settings.SaveProfileAsync(tenant, new ModelProfile { Id = "quick", Name = "Quick", Description = "routine collection", Provider = "Mock", Model = "quick-model" }, null, makeDefault: false);

        var calls = new ConcurrentQueue<(string Role, string? Profile)>();
        var prompts = new ConcurrentQueue<string>();
        string? refusal = null;
        ScriptedLlmProviderRegistry.Current = r =>
        {
            var role = PromptInspector.ExtractRole(r.Messages[0].Content!);
            calls.Enqueue((role, r.ModelProfileId));
            if (!role.Contains("Root", StringComparison.OrdinalIgnoreCase)) return Respond(Call("complete_task", new { status = "completed", summary = "done" }));

            prompts.Enqueue(r.Messages[0].Content!);
            var results = r.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
            switch (results.Count)
            {
                // The goal says Quick for collecting prices: by name, as a person would write it.
                case 0: return Respond(Call("spawn_agent", new { role = "Price Collector", goal = "Collect competitor prices", why_not_myself = "Runs in parallel with the analysis.", model = "Quick" }));
                case 1: return Respond(Call("spawn_agent", new { role = "Market Analyst", goal = "Analyse the market", why_not_myself = "Needs a separate deep pass.", model = "careful" }));
                case 2: return Respond(Call("spawn_agent", new { role = "Extra", goal = "x", why_not_myself = "Testing an unknown model.", model = "gpt-9-ultra" }));
                default:
                    refusal ??= results[2].Content;
                    // The user switches the task to Quick: agents given a model keep theirs.
                    TestModels.Selection.SetAsync(taskId, "quick").GetAwaiter().GetResult();
                    return Respond(Call("complete_task", new { status = "completed", summary = "Delegated." }));
            }
        };

        var root = await StartRootAsync(tenant, taskId, "Use Quick for collecting competitor pricing and Careful for the market analysis.");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await root.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);
        Assert.Equal(AgentStatus.Completed, (await root.GetSnapshot()).Status);

        // The root saw the models, with what each is for.
        var prompt = prompts.First();
        Assert.Contains("## MODELS", prompt);
        Assert.Contains("- quick: Quick", prompt);
        Assert.Contains("Use for: routine collection", prompt);

        var all = calls.ToList();
        Assert.All(all.Where(c => c.Role.Contains("Price", StringComparison.OrdinalIgnoreCase)), c => Assert.Equal("quick", c.Profile));
        Assert.All(all.Where(c => c.Role.Contains("Analyst", StringComparison.OrdinalIgnoreCase)), c => Assert.Equal("careful", c.Profile));
        Assert.Contains(all, c => c.Role.Contains("Price", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(all, c => c.Role.Contains("Analyst", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, c => c.Role.Contains("Extra", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("No model 'gpt-9-ultra'", refusal);
        Assert.Contains("careful", refusal);
    }

    [Fact]
    public async Task When_agents_may_not_choose_they_see_no_models_and_a_model_is_refused()
    {
        var tenant = "org-" + Guid.NewGuid().ToString("n")[..6];
        await TestModels.Settings.SaveProfileAsync(tenant, new ModelProfile { Id = "quick", Name = "Quick", Provider = "Mock", Model = "quick-model" }, null, makeDefault: true);
        await TestModels.Settings.SetAgentsMayChooseAsync(tenant, false);

        string? prompt = null;
        string? refusal = null;
        ScriptedLlmProviderRegistry.Current = r =>
        {
            prompt ??= r.Messages[0].Content;
            var results = r.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
            if (results.Count == 0) return Respond(Call("spawn_agent", new { role = "Helper", goal = "x", why_not_myself = "Testing a refused model.", model = "quick" }));
            refusal ??= results[0].Content;
            return Respond(Call("complete_task", new { status = "completed", summary = "ok" }));
        };

        var root = await StartRootAsync(tenant, Guid.NewGuid().ToString("n"), "g");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await root.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);

        Assert.DoesNotContain("## MODELS", prompt);
        Assert.Contains("doesn't let agents choose models", refusal);
        Assert.Empty((await root.GetSnapshot()).Children);
    }
}
