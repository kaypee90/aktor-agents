using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Skills;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Skills reach agents: an agent's prompt lists its organization's enabled skills (and only
/// those), and load_skill / read_skill_file return a skill's instructions and files, while another
/// organization's skill can't be loaded even by name.
/// </summary>
public sealed class SkillsTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private readonly ConcurrentQueue<LlmCompletionRequest> _requests = new();

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

    [Fact]
    public async Task Agents_see_and_load_their_organizations_skills_only()
    {
        var mine = "org-" + Guid.NewGuid().ToString("n")[..6];
        var other = "org-" + Guid.NewGuid().ToString("n")[..6];
        await TestSkills.Store.SaveAsync(mine, SkillPackage.FromParts("postmortems", "How we write postmortems. Use for incident write-ups.",
            "Always include a timeline and five whys.", [new SkillFile("reference/template.md", "# Postmortem template")]));
        await TestSkills.Store.SaveAsync(mine, SkillPackage.FromParts("retired-skill", "Old process.", "Don't use.", null));
        await TestSkills.Store.SetEnabledAsync(mine, "retired-skill", false);
        await TestSkills.Store.SaveAsync(other, SkillPackage.FromParts("secret-sauce", "Another org's skill.", "Confidential.", null));

        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            var done = r.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolName).ToList();
            ToolCall next = done.Count switch
            {
                0 => Call("load_skill", new { name = "postmortems" }),
                1 => Call("read_skill_file", new { name = "postmortems", path = "reference/template.md" }),
                2 => Call("load_skill", new { name = "secret-sauce" }),
                _ => Call("complete_task", new { status = "completed", summary = "Wrote the postmortem." })
            };
            return new LlmCompletionResponse { ToolCalls = [next], FinishReason = LlmFinishReason.ToolCalls };
        };

        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await registry.RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = mine });
        var grain = _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId);
        await grain.Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "Write the postmortem for last night's outage.",
            // Created without the skill tools in its list: they come with the organization's skills.
            AllowedTools = ["complete_task"],
            Budget = new ResourceBudget(),
            TaskId = Guid.NewGuid().ToString("n"),
            TenantId = mine,
            AutoStart = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await grain.GetSnapshot()).Status != AgentStatus.Completed) await Task.Delay(50);
        Assert.Equal(AgentStatus.Completed, (await grain.GetSnapshot()).Status);

        var first = _requests.First();
        var system = first.Messages[0].Content!;
        Assert.Contains("## SKILLS", system);
        Assert.Contains("- postmortems: How we write postmortems.", system);
        Assert.DoesNotContain("retired-skill", system);
        Assert.DoesNotContain("secret-sauce", system);
        Assert.Contains(first.Tools, t => t.Name == "load_skill");
        Assert.Contains(first.Tools, t => t.Name == "read_skill_file");
        // Only the list is in the prompt: the instructions arrive when the skill is loaded.
        Assert.DoesNotContain("five whys", system);

        var results = _requests.Last().Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Contains("five whys", results[0].Content);
        Assert.Contains("reference/template.md", results[0].Content);
        Assert.Contains("# Postmortem template", results[1].Content);
        Assert.Contains("No skill named", results[2].Content);
        Assert.DoesNotContain("Confidential", results[2].Content);
    }

    [Fact]
    public async Task Without_skills_nothing_is_added()
    {
        var org = "org-" + Guid.NewGuid().ToString("n")[..6];
        ScriptedLlmProviderRegistry.Current = r =>
        {
            _requests.Enqueue(r);
            return new LlmCompletionResponse { ToolCalls = [Call("complete_task", new { status = "completed", summary = "done" })], FinishReason = LlmFinishReason.ToolCalls };
        };

        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(0);
        var rootId = $"root-{Guid.NewGuid():n}"[..14];
        await registry.RegisterAsync(new AgentDirectoryEntry { AgentId = rootId, Role = "Root Agent", Goal = "g", RootAgentId = rootId, TenantId = org });
        await _cluster.GrainFactory.GetGrain<IAgentGrain>(rootId).Initialize(new AgentInitializationRequest
        {
            AgentId = rootId, RootAgentId = rootId, Name = "Root", Role = "Root Agent", Goal = "g", AllowedTools = ["complete_task"],
            Budget = new ResourceBudget(), TaskId = Guid.NewGuid().ToString("n"), TenantId = org, AutoStart = true
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _requests.IsEmpty) await Task.Delay(50);
        var request = Assert.Single(_requests);
        Assert.DoesNotContain("## SKILLS", request.Messages[0].Content);
        Assert.DoesNotContain(request.Tools, t => t.Name == "load_skill");
    }
}
