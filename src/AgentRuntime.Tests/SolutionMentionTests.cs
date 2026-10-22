using System.Text.Json.Nodes;
using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Integrations;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Solutions;

namespace AgentRuntime.Tests;

/// <summary>@mentions while building a solution (docs/solutions.md#mentions): the designer is told
/// what each mention in a message names, the validator flags mentions that name nothing, and an
/// agent pointed to a connector is told which tools those are.</summary>
public sealed class SolutionMentionTests
{
    private static JsonObject Draft() => JsonNode.Parse("""
        {
          "format": 1, "name": "my-app", "title": "My app", "version": "0.1.0",
          "connectors": [{ "name": "sms", "plugin": "twilio-sms" }],
          "workspaces": [{ "key": "requests", "name": "Requests", "goal": "Handle requests.",
            "pipeline": { "stages": [{ "stage_id": "triage", "name": "Triage", "instructions": "Triage it." }] } }],
          "knowledge": [{ "key": "Refund Policy", "text": "30 days." }],
          "assistant": { "name": "Helper", "instructions": "Help." }
        }
        """)!.AsObject();

    [Fact]
    public void The_designer_is_told_what_each_mention_names()
    {
        var lines = SolutionDesignPrompt.DescribeMentions(
            "Have @triage use @skill:triage-rules, @knowledge:refund-policy, @connector:sms and @connector:team_slack in @workspace:requests; tell @assistant. Mail me@x.com, ask @nobody.",
            Draft(), [new DraftSkill { Name = "triage-rules", Description = "How to triage" }],
            new SolutionDesignContext { GlobalConnectors = [("team_slack", "slack")] });

        Assert.Contains(lines, l => l.StartsWith("@triage:") && l.Contains("'Triage' stage of workspace requests"));
        Assert.Contains(lines, l => l.StartsWith("@skill:triage-rules:") && l.Contains("solution's skill"));
        Assert.Contains(lines, l => l.StartsWith("@knowledge:refund-policy:") && l.Contains("'Refund Policy'"));
        Assert.Contains(lines, l => l.StartsWith("@connector:sms:") && l.Contains("sms__<tool>"));
        Assert.Contains(lines, l => l.StartsWith("@connector:team_slack:") && l.Contains("global \"team_slack\""));
        Assert.Contains(lines, l => l.StartsWith("@workspace:requests:"));
        Assert.Contains(lines, l => l.StartsWith("@assistant:"));
        Assert.DoesNotContain(lines, l => l.Contains("nobody") || l.Contains("x.com"));

        var request = SolutionDesignPrompt.BuildRequest(Draft(), [], [], "Use @connector:sms", new SolutionDesignContext(), null, 1000);
        Assert.Contains("What the @mentions in it refer to:\n- @connector:sms:", request.Messages[^1].Content!.Replace("\r", ""));
        Assert.Contains("@connector:<name>", request.Messages[0].Content);
    }

    [Fact]
    public void Mentions_that_name_nothing_in_the_package_are_warnings()
    {
        var (m, errors) = SolutionManifestReader.Read(new SolutionBundle
        {
            Manifest = """
                format: 1
                name: my-app
                version: 0.1.0
                connectors:
                  - { name: sms, plugin: twilio-sms }
                  - { name: chat, plugin: slack }
                workspaces:
                  - key: requests
                    name: Requests
                    goal: Handle requests with @skill:triage and @knowledge:refund-policy.
                    connectors: [sms]
                    pipeline:
                      stages:
                        - { stage_id: triage, name: Triage, instructions: "Text them with @connector:sms, post in @connector:chat, page with @connector:pager, follow @skill:missing." }
                knowledge:
                  - { key: Refund Policy, text: 30 days. }
                skills:
                  - { path: skills/triage/SKILL.md }
                assistant:
                  name: Helper
                  instructions: Use @skill:triage and @connector:sms.
                  connectors: [sms]
                """,
            Files = new() { ["skills/triage/SKILL.md"] = "---\nname: triage\ndescription: How to triage\n---\n\nUrgent first.\n" }
        });
        Assert.Empty(errors);
        var warnings = SolutionValidator.Validate(m!, _ => true, new PipelineOptions(), new Dictionary<string, string> { ["skills/triage/SKILL.md"] = "---\nname: triage\ndescription: How to triage\n---\n\nUrgent first.\n" }).Warnings;

        Assert.Contains("Workspace 'requests' mentions @connector:chat but doesn't use that connector, so its agents won't have its tools.", warnings);
        Assert.Contains("Workspace 'requests' mentions @connector:pager, which isn't declared under connectors.", warnings);
        Assert.Contains(warnings, w => w.StartsWith("Workspace 'requests' mentions @skill:missing"));
        Assert.DoesNotContain(warnings, w => w.Contains("@skill:triage") && w.StartsWith("Workspace"));
        Assert.DoesNotContain(warnings, w => w.Contains("@knowledge:refund-policy"));
        Assert.DoesNotContain(warnings, w => w.Contains("@connector:sms"));
        Assert.Contains(warnings, w => w.StartsWith("assistant mentions @skill:triage: the solution's skills go to its workspaces"));
    }

    [Fact]
    public void An_agent_pointed_to_a_connector_is_told_which_tools_are_its()
    {
        var section = new ConnectorMentionsSection();
        var state = new AgentState { AgentId = "a1", Name = "A", Role = "worker", Goal = "Text the tenant with @connector:sms, then post to @connector:slack." };
        var context = new AgentPromptContext
        {
            State = state,
            AvailableTools = [new ToolDefinitionSummary("sms__send", "Send an SMS"), new ToolDefinitionSummary("data__list_request", "List")],
            AutonomyLevel = AutonomyLevel.Autonomous,
            EnvironmentSummary = "env"
        };
        var text = section.Render(context);
        Assert.Contains("@connector:sms: use its tools (sms__send)", text);
        Assert.Contains("@connector:slack: you have no slack__ tools", text);

        state.Goal = "Nothing named.";
        Assert.Empty(section.Render(context));
        Assert.Contains(Mentions.Describe(["Post with @connector:slack"], [], []), l => l.StartsWith("@connector:slack:") && l.Contains("slack__<tool>"));
    }
}
