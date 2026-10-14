using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Integrations;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;
using Orleans.TestingHost;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// n8n-style workflow pieces end to end (docs/workflows.md): action stages call a connection's tool
/// with arguments filled in from the run's data (once, or once per item), script stages transform
/// data, outputs flow to later stages as JSON, and form triggers start runs with typed answers.
/// </summary>
public sealed class WorkflowStageTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        TestSideEffects.Reset();
        FakeCrmPlugin.Reset();
        FakeScriptRunner.Seen.Clear();
        _cluster = await DurableTestCluster.StartAsync();
    }

    public async Task DisposeAsync()
    {
        ScriptedLlmProviderRegistry.Current = null;
        await _cluster.DisposeAsync();
    }

    private IWorkspaceGrain Workspace(string id) => _cluster.Client.GetGrain<IWorkspaceGrain>(id);

    private async Task<string> CreateAsync(params PipelineStage[] stages)
    {
        var id = WorkspaceIds.New();
        await Workspace(id).Create(new WorkspaceCreationRequest { Name = "Ops", Goal = "Keep the CRM tidy.", Pipeline = new PipelineDefinition { Stages = [.. stages] } });
        var c = await Workspace(id).AddConnection(new ConnectionRequest
        {
            PluginId = "fake-crm",
            Name = "crm",
            Settings = new() { ["region"] = "eu" },
            Secrets = new() { ["api_key"] = "k" }
        });
        Assert.True(c.Success, c.Message);
        return id;
    }

    private async Task<PipelineRunView> WaitForRunAsync(string runId, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        PipelineRunView? view = null;
        while (DateTime.UtcNow < deadline)
        {
            view = await _cluster.Client.GetGrain<IPipelineRunGrain>(runId).GetView();
            if (view is not null && view.Status is not (PipelineRunStatus.Queued or PipelineRunStatus.Running)) return view;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Run didn't finish: {JsonSerializer.Serialize(view)}");
    }

    private static PipelineStage Action(string id, string tool, string arguments, string? forEach = null, params string[] inputs) =>
        new() { StageId = id, Name = id, Kind = StageKind.Action, Tool = tool, Arguments = arguments, ForEach = forEach, Inputs = [.. inputs], Retries = 0 };

    private static PipelineStage Script(string id, string code, params string[] inputs) =>
        new() { StageId = id, Name = id, Kind = StageKind.Script, Language = ScriptLanguages.Python, Code = code, Inputs = [.. inputs], Retries = 0 };

    [Fact]
    public async Task An_action_gets_the_runs_data_a_script_transforms_its_output_and_an_agent_reads_the_result_without_extra_model_calls()
    {
        FakeScriptRunner.Handlers["tier-upper"] = data => new JsonObject
        {
            ["who"] = data["previous"]!["customer"]!.DeepClone(),
            ["tier"] = data["previous"]!["tier"]!.GetValue<string>().ToUpperInvariant(),
            ["asked_for"] = data["input"]!["customer_id"]!.DeepClone()
        };
        var calls = 0;
        ScriptedLlmProviderRegistry.Current = r =>
        {
            Interlocked.Increment(ref calls);
            var context = r.Messages.First(m => m.Role == ChatRole.User).Content ?? "";
            // The agent stage sees the script's output among its inputs' results.
            var tier = context.Contains("\"tier\":\"GOLD\"", StringComparison.Ordinal) ? "GOLD" : "unknown";
            return new LlmCompletionResponse
            {
                ToolCalls = [new ToolCall { Id = "c1", Name = "complete_task", ArgumentsJson = JsonSerializer.Serialize(new { status = "completed", summary = $"Customer is {tier}." }) }],
                FinishReason = LlmFinishReason.ToolCalls
            };
        };

        var id = await CreateAsync(
            Action("lookup", "crm__lookup_customer", """{ "id": "{{ input.customer_id }}", "note": "Looking up {{ input.customer_id }} for run {{ run.number }}" }"""),
            Script("shape", "tier-upper", "lookup"),
            new PipelineStage { StageId = "report", Name = "report", Role = "Reporter", Instructions = "Report the customer's tier.", Inputs = ["shape"], Retries = 0 });

        var started = await Workspace(id).StartRunWithData("Look up customer C-42", """{ "customer_id": "C-42" }""", "user-1");
        Assert.True(started.Success, started.Message);
        var run = await WaitForRunAsync(started.RunId!);

        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        var call = Assert.Single(FakeCrmPlugin.ToolCalls);
        Assert.Equal("lookup_customer", call.Tool);
        var args = JsonNode.Parse(call.Args)!;
        Assert.Equal("C-42", args["id"]!.GetValue<string>());
        Assert.Equal("Looking up C-42 for run 1", args["note"]!.GetValue<string>());

        var shaped = JsonNode.Parse(run.Stages.Single(s => s.StageId == "shape").OutputJson!)!;
        Assert.Equal("GOLD", shaped["tier"]!.GetValue<string>());
        Assert.Equal("C-42", shaped["asked_for"]!.GetValue<string>());
        Assert.Equal("ran tier-upper", run.Stages.Single(s => s.StageId == "shape").Logs);
        Assert.Equal(StageKind.Action, run.Stages.Single(s => s.StageId == "lookup").Kind);

        // Only the agent stage called the model.
        Assert.Equal(1, calls);
        Assert.Contains("Customer is GOLD.", run.Summary);
        // The agent's report is data for later stages too.
        Assert.Equal("Customer is GOLD.", JsonNode.Parse(run.Stages.Single(s => s.StageId == "report").OutputJson!)!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task For_each_calls_the_tool_once_per_item_and_the_output_is_the_list_of_results()
    {
        var id = await CreateAsync(Action("lookup-all", "crm__lookup_customer", """{ "id": "{{ item.id }}", "position": "{{ index }}" }""", forEach: "input.customers"));

        var started = await Workspace(id).StartRunWithData("Look up three", """{ "customers": [ { "id": "A" }, { "id": "B" }, { "id": "C" } ] }""", "user-1");
        var run = await WaitForRunAsync(started.RunId!);

        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        var ids = FakeCrmPlugin.ToolCalls.Select(c => JsonNode.Parse(c.Args)!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(["A", "B", "C"], ids);
        // A whole-value expression keeps its type: the index is a number, not text.
        Assert.Equal(2, JsonNode.Parse(FakeCrmPlugin.ToolCalls.Last().Args)!["position"]!.GetValue<int>());
        Assert.Equal(3, JsonNode.Parse(run.Stages[0].OutputJson!)!.AsArray().Count);
    }

    [Fact]
    public async Task A_missing_tool_or_failing_script_fails_its_stage_with_a_readable_reason()
    {
        var id = await CreateAsync(
            Action("gone", "crm__no_such_tool", "{}"),
            Script("broken", "no-such-handler", "gone") with { OnFailure = StageFailurePolicy.FailRun });

        var started = await Workspace(id).StartRun("go", "user-1");
        var run = await WaitForRunAsync(started.RunId!);

        Assert.Equal(PipelineRunStatus.Failed, run.Status);
        var gone = run.Stages.Single(s => s.StageId == "gone");
        Assert.Equal(StageRunStatus.Failed, gone.Status);
        Assert.Contains("no enabled connection tool 'crm__no_such_tool'", gone.Error);
        Assert.Equal(StageRunStatus.Skipped, run.Stages.Single(s => s.StageId == "broken").Status);
    }

    [Fact]
    public async Task A_failing_script_is_retried_and_then_can_let_the_run_carry_on()
    {
        var attempts = 0;
        FakeScriptRunner.Handlers["flaky"] = _ => Interlocked.Increment(ref attempts) < 2 ? throw new InvalidOperationException("boom") : JsonValue.Create("ok");
        var id = await CreateAsync(Script("flaky", "flaky") with { Retries = 1 });

        var run = await WaitForRunAsync((await Workspace(id).StartRun("go", "user-1")).RunId!);

        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        Assert.Equal(2, run.Stages[0].Attempts);
        Assert.Equal("\"ok\"", run.Stages[0].OutputJson);
    }

    [Fact]
    public async Task A_form_checks_answers_types_them_and_starts_one_run_per_submission()
    {
        FakeScriptRunner.Handlers["echo-input"] = data => data["input"]!.DeepClone();
        var id = await CreateAsync(Script("echo", "echo-input"));
        var added = await Workspace(id).AddTrigger(new TriggerSpec
        {
            Kind = TriggerKind.Form,
            Name = "New lead",
            Instruction = "Qualify this lead.",
            Form = new FormDefinition
            {
                Title = "New lead",
                Fields =
                [
                    new FormField { Key = "email", Label = "Email", Type = FormFieldTypes.Email, Required = true },
                    new FormField { Key = "seats", Label = "Seats", Type = FormFieldTypes.Number },
                    new FormField { Key = "plan", Label = "Plan", Type = FormFieldTypes.Select, Options = ["Team", "Business"], Required = true },
                    new FormField { Key = "newsletter", Label = "Newsletter", Type = FormFieldTypes.Checkbox }
                ]
            }
        }, "user", "", revealSecret: true);
        Assert.True(added.Success, added.Message);
        var triggerId = JsonNode.Parse(added.ResultJson!)!["trigger_id"]!.GetValue<string>();

        var form = await Workspace(id).GetForm(triggerId);
        Assert.NotNull(form);
        Assert.True(form.Accepting);
        Assert.Equal(4, form.Form.Fields.Count);

        var bad = await Workspace(id).SubmitForm(new FormSubmission { TriggerId = triggerId, AnswersJson = """{ "email": "nope", "plan": "Enterprise" }""", SubmittedBy = "user-1" });
        Assert.False(bad.Success);
        Assert.Equal(["email", "plan"], bad.FieldErrors.Keys.Order().ToList());

        var answers = """{ "email": "ama@example.com", "seats": "12", "plan": "Team", "newsletter": true }""";
        var ok = await Workspace(id).SubmitForm(new FormSubmission { TriggerId = triggerId, AnswersJson = answers, SubmittedBy = "user-1", SubmittedByName = "ama@example.com", SubmissionId = "s-1" });
        Assert.True(ok.Success, ok.Message);
        var again = await Workspace(id).SubmitForm(new FormSubmission { TriggerId = triggerId, AnswersJson = answers, SubmittedBy = "user-1", SubmissionId = "s-1" });
        Assert.Equal(ok.RunId, again.RunId);

        var run = await WaitForRunAsync(ok.RunId!);
        Assert.Equal(PipelineRunStatus.Completed, run.Status);
        Assert.Equal("form", run.Source);
        Assert.Contains("Qualify this lead.", run.Input);
        Assert.Contains("- Plan: Team", run.Input);
        // Typed: the number is a number and the checkbox a boolean.
        var input = JsonNode.Parse(run.Stages[0].OutputJson!)!;
        Assert.Equal(12m, input["seats"]!.GetValue<decimal>());
        Assert.True(input["newsletter"]!.GetValue<bool>());
        Assert.Equal("ama@example.com", input["email"]!.GetValue<string>());

        var snapshot = await Workspace(id).GetSnapshot();
        Assert.Single(snapshot!.Runs);
    }
}
