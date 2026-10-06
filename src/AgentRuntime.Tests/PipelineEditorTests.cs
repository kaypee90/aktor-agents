using AgentRuntime.Pipelines;

namespace AgentRuntime.Tests;

/// <summary>Pipeline rules and edits: stages can be added and removed anywhere and the pipeline
/// stays joined up; anything that can't run (loops, unknown inputs, limits) is refused.</summary>
public class PipelineEditorTests
{
    private static readonly PipelineOptions Options = new();

    private static PipelineStage Stage(string id, params string[] inputs) =>
        new() { StageId = id, Name = id.ToUpperInvariant(), Instructions = $"Do {id}.", Inputs = [.. inputs] };

    /// <summary>research → write → review</summary>
    private static PipelineDefinition Linear() => new() { Version = 1, Stages = [Stage("research"), Stage("write", "research"), Stage("review", "write")] };

    private static List<string> InputsOf(PipelineDefinition p, string id) => p.Find(id)!.Inputs;

    [Fact]
    public void A_linear_pipeline_is_valid_and_ordered()
    {
        var p = Linear();
        Assert.Empty(PipelineValidator.Validate(p, Options));
        Assert.Equal(["research", "write", "review"], PipelineValidator.TopologicalOrder(p)!.Select(s => s.StageId));
        Assert.Equal(["review"], p.Outputs().Select(s => s.StageId));
    }

    [Fact]
    public void Loops_unknown_inputs_and_duplicate_ids_are_refused()
    {
        var loop = new PipelineDefinition { Stages = [Stage("a", "b"), Stage("b", "a")] };
        Assert.Contains(PipelineValidator.Validate(loop, Options), e => e.Contains("loop"));

        var unknown = new PipelineDefinition { Stages = [Stage("a", "ghost")] };
        Assert.Contains(PipelineValidator.Validate(unknown, Options), e => e.Contains("ghost"));

        var duplicate = new PipelineDefinition { Stages = [Stage("a"), Stage("a")] };
        Assert.Contains(PipelineValidator.Validate(duplicate, Options), e => e.Contains("Two stages"));

        Assert.Contains(PipelineValidator.Validate(new PipelineDefinition(), Options), e => e.Contains("at least one"));
    }

    [Fact]
    public void Limits_and_capabilities_come_from_the_runtime()
    {
        var p = new PipelineDefinition { Stages = [Stage("a") with { MaxHelpers = 99, Capabilities = ["teleport"] }] };
        var errors = PipelineValidator.Validate(p, Options);
        Assert.Contains(errors, e => e.Contains("helpers"));
        Assert.Contains(errors, e => e.Contains("teleport"));

        var tooMany = new PipelineDefinition { Stages = Enumerable.Range(0, Options.MaxStages + 1).Select(i => Stage($"s{i}")).ToList() };
        Assert.Contains(PipelineValidator.Validate(tooMany, Options), e => e.Contains("at most"));
    }

    [Fact]
    public void Adding_after_a_stage_inserts_it_before_what_that_stage_fed()
    {
        var result = PipelineEditor.Apply(Linear(),
            [new PipelineEditOp { Op = PipelineEditOps.AddStage, After = "write", Stage = new PipelineStagePatch { Name = "Security Reviewer", Instructions = "Check for secrets." } }],
            Options);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var p = result.Pipeline!;
        Assert.Equal(["write"], InputsOf(p, "security-reviewer"));
        Assert.Equal(["security-reviewer"], InputsOf(p, "review"));
        Assert.Contains("Added 'Security Reviewer' after 'WRITE'", result.Changes);
    }

    [Fact]
    public void Adding_before_the_first_stage_makes_a_new_entry_stage()
    {
        var p = PipelineEditor.Apply(Linear(),
            [new PipelineEditOp { Op = PipelineEditOps.AddStage, Before = "research", Stage = new PipelineStagePatch { Name = "Intake", Instructions = "Clarify the request." } }],
            Options).Pipeline!;

        Assert.Empty(InputsOf(p, "intake"));
        Assert.Equal(["intake"], InputsOf(p, "research"));
    }

    [Fact]
    public void Adding_with_inputs_makes_a_parallel_branch_that_merges()
    {
        var result = PipelineEditor.Apply(Linear(),
        [
            new PipelineEditOp { Op = PipelineEditOps.AddStage, Stage = new PipelineStagePatch { StageId = "data", Name = "Data analyst", Instructions = "Pull the numbers.", Inputs = ["research"] } },
            new PipelineEditOp { Op = PipelineEditOps.Connect, From = "data", To = "write" }
        ], Options);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var p = result.Pipeline!;
        Assert.Equal(["research", "data"], InputsOf(p, "write"));
        // research fans out to write and data; write waits for both.
        Assert.Equal(["write", "data"], p.DependentsOf("research").Select(s => s.StageId));
    }

    [Fact]
    public void Removing_a_stage_joins_its_inputs_to_its_dependents()
    {
        var p = PipelineEditor.Apply(Linear(), [new PipelineEditOp { Op = PipelineEditOps.RemoveStage, StageId = "write" }], Options).Pipeline!;
        Assert.Null(p.Find("write"));
        Assert.Equal(["research"], InputsOf(p, "review"));
    }

    [Fact]
    public void An_edit_that_would_loop_is_refused_and_nothing_changes()
    {
        var original = Linear();
        var result = PipelineEditor.Apply(original, [new PipelineEditOp { Op = PipelineEditOps.Connect, From = "review", To = "research" }], Options);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("loop"));
        Assert.Empty(InputsOf(original, "research"));
    }

    [Fact]
    public void Updates_change_only_the_named_fields_and_ids_stay_unique()
    {
        var p = PipelineEditor.Apply(Linear(),
        [
            new PipelineEditOp { Op = PipelineEditOps.UpdateStage, StageId = "write", Stage = new PipelineStagePatch { MaxHelpers = 2, Capabilities = ["Research", "filesystem"] } },
            new PipelineEditOp { Op = PipelineEditOps.AddStage, After = "review", Stage = new PipelineStagePatch { Name = "Write", Instructions = "Polish." } }
        ], Options).Pipeline!;

        var write = p.Find("write")!;
        Assert.Equal(2, write.MaxHelpers);
        Assert.Equal(["research", "filesystem"], write.Capabilities);
        Assert.Equal("Do write.", write.Instructions);
        Assert.NotNull(p.Find("write-2"));
    }

    [Fact]
    public void Unknown_stages_and_ops_are_reported()
    {
        var result = PipelineEditor.Apply(Linear(),
        [
            new PipelineEditOp { Op = PipelineEditOps.RemoveStage, StageId = "nope" },
            new PipelineEditOp { Op = "rename_everything" }
        ], Options);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("nope"));
        Assert.Contains(result.Errors, e => e.Contains("rename_everything"));
    }

    [Fact]
    public void A_stage_added_on_the_canvas_keeps_its_place_and_a_removed_one_loses_it()
    {
        var placed = Linear() with { Layout = new() { ["research"] = new(0, 0), ["write"] = new(300, 0) } };
        var added = PipelineEditor.Apply(placed,
            [new PipelineEditOp { Op = PipelineEditOps.AddStage, Stage = new PipelineStagePatch { Name = "Fact check", Instructions = "Check.", Inputs = ["research"] }, Position = new(150, 200.6) }],
            Options);
        Assert.True(added.Success, string.Join("; ", added.Errors));
        Assert.Equal(new StagePosition(150, 201), added.Pipeline!.Layout["fact-check"]);
        Assert.Equal(new StagePosition(300, 0), added.Pipeline.Layout["write"]);

        var removed = PipelineEditor.Apply(added.Pipeline, [new PipelineEditOp { Op = PipelineEditOps.RemoveStage, StageId = "fact-check" }], Options);
        Assert.False(removed.Pipeline!.Layout.ContainsKey("fact-check"));
    }

    [Fact]
    public void Layout_keeps_only_existing_stages_with_sane_coordinates()
    {
        var kept = PipelineLayout.Keep(new Dictionary<string, StagePosition>
        {
            ["research"] = new(double.NaN, 0),
            ["write"] = new(1e12, -5.5),
            ["ghost"] = new(1, 1)
        }, Linear().Stages);
        Assert.Equal([("write", new StagePosition(PipelineLayout.MaxCoordinate, -6))], kept.Select(p => (p.Key, p.Value)));
    }
}

/// <summary>@mentions: a stage, a model or a provider means one thing to whichever model reads it.</summary>
public class MentionTests
{
    private static readonly List<PipelineStage> Stages =
        [new() { StageId = "diagnose", Name = "Diagnose", Role = "Incident analyst" }, new() { StageId = "remediate", Name = "Remediate" }];

    private static readonly List<MentionableModel> Models =
        [new("server", "Server default", "Ollama", "qwen3:8b"), new("claude-fast", "Claude fast", "Anthropic", "claude-haiku-4-5")];

    [Fact]
    public void Handles_are_found_but_not_inside_email_addresses()
    {
        Assert.Equal(["diagnose", "claude-fast"], Mentions.Find("Use @claude-fast?? no: @diagnose, then @claude-fast. Mail ops@example.com").OrderBy(h => h.Length));
        Assert.Empty(Mentions.Find("ops@example.com and @"));
    }

    [Fact]
    public void Stages_models_providers_and_the_default_model_are_described_and_unknown_handles_ignored()
    {
        var lines = Mentions.Describe(["Run @diagnose on @claude-fast, or @default-model, ask @anthropic, cc @someone"], Stages, Models);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("@diagnose:") && l.Contains("'Diagnose' stage (Incident analyst)"));
        Assert.Contains(lines, l => l.StartsWith("@claude-fast:") && l.Contains("model_profile_id to \"claude-fast\""));
        Assert.Contains(lines, l => l.StartsWith("@default-model:") && l.Contains("\"server\""));
        Assert.Contains(lines, l => l.StartsWith("@anthropic:") && l.Contains("'Claude fast'"));
    }

    [Fact]
    public void The_editor_is_told_the_models_and_what_mentions_mean()
    {
        var pipeline = new PipelineDefinition { Version = 1, Stages = Stages };
        var request = PipelineDesignPrompt.BuildRequest(pipeline, "Use @claude-fast for @remediate", "Incidents", [], new PipelineOptions(), null, 1000, Models);
        var system = request.Messages[0].Content;
        var user = request.Messages[1].Content;
        Assert.Contains("claude-fast = Claude fast (Anthropic claude-haiku-4-5)", system);
        Assert.Contains("@remediate: the 'Remediate' stage", user);
        Assert.Contains("model_profile_id", request.Tools[0].JsonSchema);
    }
}
