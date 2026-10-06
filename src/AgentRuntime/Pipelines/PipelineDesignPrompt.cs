using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.LLM;
using AgentRuntime.Tools;

namespace AgentRuntime.Pipelines;

/// <summary>
/// The natural-language pipeline editor: one LLM call that turns "add a security reviewer after
/// Backend" (or, for a new workspace, a description of what it's for) into
/// <see cref="PipelineEditOp"/>s. The model only proposes; <see cref="PipelineEditor"/> applies and
/// validates, and a person applies the result.
/// </summary>
public static class PipelineDesignPrompt
{
    public const string ToolName = "propose_pipeline_changes";

    /// <summary>Recognisable in the system prompt (the Mock provider answers it heuristically).</summary>
    public const string SystemMarker = "You design agent pipelines.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static readonly LlmToolDefinition Tool = new()
    {
        Name = ToolName,
        Description = "Propose the changes to the pipeline that do what was asked.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "One sentence: what the changes do." },
            "changes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "op": { "type": "string", "enum": ["add_stage", "update_stage", "remove_stage", "connect", "disconnect"] },
                  "stage_id": { "type": "string", "description": "update_stage / remove_stage: the stage's id." },
                  "after": { "type": "string", "description": "add_stage: insert after this stage id (between it and what it fed)." },
                  "before": { "type": "string", "description": "add_stage: insert before this stage id." },
                  "from": { "type": "string", "description": "connect / disconnect: the stage whose result flows..." },
                  "to": { "type": "string", "description": "...into this stage." },
                  "stage": {
                    "type": "object",
                    "description": "add_stage: the new stage. update_stage: only the fields to change.",
                    "properties": {
                      "stage_id": { "type": "string", "description": "add_stage only: lowercase-dashed id, e.g. security-review." },
                      "name": { "type": "string" },
                      "role": { "type": "string" },
                      "instructions": { "type": "string", "description": "What the stage's agent does with the run's input and its inputs' results." },
                      "inputs": { "type": "array", "items": { "type": "string" }, "description": "Stage ids whose results it needs (for parallel branches)." },
                      "capabilities": { "type": "array", "items": { "type": "string" } },
                      "model_profile_id": { "type": "string", "description": "Run this stage on one of the listed models (its id); \"\" for the workspace's model." },
                      "max_helpers": { "type": "integer" },
                      "may_message_stages": { "type": "boolean" },
                      "retries": { "type": "integer" },
                      "on_failure": { "type": "string", "enum": ["fail_run", "continue"] }
                    }
                  }
                },
                "required": ["op"]
              }
            }
          },
          "required": ["summary", "changes"]
        }
        """
    };

    /// <param name="current">The pipeline now; null (or no stages) to design one from scratch.</param>
    /// <param name="connectionTools">Tools of the workspace's connections, which stages can name in their instructions.</param>
    /// <param name="models">Models a stage can run on (the organization's profiles and the server's).</param>
    public static LlmCompletionRequest BuildRequest(PipelineDefinition? current, string request, string workspacePurpose,
        IReadOnlyList<string> connectionTools, PipelineOptions limits, string? model, int maxOutputTokens,
        IReadOnlyCollection<MentionableModel>? models = null)
    {
        models ??= [];
        var system = new StringBuilder();
        system.AppendLine(SystemMarker);
        system.AppendLine("""
            A pipeline is a graph of stages. Each stage is an AI agent with a role, instructions and tools; it
            starts when all its inputs (other stages) have finished, gets their results, does its part, and
            reports. Stages with no inputs start with the run; stages nobody takes as input give the run's
            result. Runs are started by a person or a trigger with an input (a task, an alert, a payload).

            Rules for good pipelines:
            - One stage per distinct job. Don't split work that one agent does well in a few steps; every
              stage costs a model call per step.
            - Use parallel branches (several stages taking the same input) for independent work, and a stage
              that takes all of them as inputs to combine the results.
            - Instructions say what to produce, for whom, and to what standard, in two to four sentences.
            - Give a stage helpers (max_helpers) only for large, splittable work.
            - Change only what was asked. Keep ids of existing stages.
            """);
        system.AppendLine($"Limits: at most {limits.MaxStages} stages, {limits.MaxHelpersPerStage} helpers per stage, {limits.MaxRetries} retries.");
        system.AppendLine($"Capabilities (grant tools): {string.Join(", ", AgentToolCatalog.KnownCapabilities)}. Every stage can read and write the run's files.");
        if (connectionTools.Count > 0) system.AppendLine($"Connected services' tools a stage can use: {string.Join(", ", connectionTools.Take(60))}.");
        if (models.Count > 1)
        {
            system.AppendLine("Models a stage can run on (set model_profile_id only when asked to; otherwise stages use the workspace's model): " +
                              string.Join("; ", models.Select(m => $"{m.Id} = {m.Name} ({m.Provider} {m.Model})")) + ".");
        }

        var user = new StringBuilder();
        user.AppendLine($"The workspace's purpose: {workspacePurpose}");
        user.AppendLine();
        if (current is null || current.Stages.Count == 0)
        {
            user.AppendLine("There is no pipeline yet: design one (add_stage for every stage, with stage_id and inputs).");
        }
        else
        {
            user.AppendLine("The pipeline now:");
            foreach (var stage in PipelineValidator.TopologicalOrder(current) ?? current.Stages)
            {
                user.AppendLine($"- {stage.StageId} \"{stage.Name}\" ({stage.EffectiveRole})" +
                                (stage.Inputs.Count > 0 ? $" ← {string.Join(", ", stage.Inputs)}" : " (entry)") +
                                $": {Clip(stage.Instructions, 300)}" +
                                (stage.MaxHelpers > 0 ? $" [helpers: {stage.MaxHelpers}]" : "") +
                                (stage.ModelProfileId is { } profile ? $" [model: {profile}]" : "") +
                                (stage.Capabilities.Count > 0 ? $" [capabilities: {string.Join(", ", stage.Capabilities)}]" : ""));
            }
        }

        user.AppendLine();
        user.AppendLine($"What the person wants: {request.Trim()}");
        var mentions = Mentions.Describe([request], current?.Stages ?? [], models);
        if (mentions.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("What their @mentions refer to:");
            foreach (var line in mentions) user.AppendLine($"- {line}");
        }
        user.AppendLine();
        user.AppendLine($"Call {ToolName} with the changes.");

        return new LlmCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = ChatRole.System, Content = system.ToString() },
                new ChatMessage { Role = ChatRole.User, Content = user.ToString() }
            ],
            Tools = [Tool],
            Model = model,
            MaxTokens = maxOutputTokens,
            Temperature = 0.2
        };
    }

    public sealed record Proposal(string Summary, List<PipelineEditOp> Ops);

    /// <summary>The changes the model proposed; null with a reason if it proposed none or nonsense.</summary>
    public static (Proposal? Proposal, string? Error) Parse(LlmCompletionResponse response)
    {
        var call = response.ToolCalls?.FirstOrDefault(c => c.Name == ToolName);
        if (call is null) return (null, string.IsNullOrWhiteSpace(response.Content) ? "The model proposed no changes." : Clip(response.Content, 500));

        try
        {
            var parsed = JsonSerializer.Deserialize<ProposalJson>(call.ArgumentsJson, Json);
            if (parsed?.Changes is not { Count: > 0 } changes) return (null, "The model proposed no changes.");
            var ops = changes.Select(c => new PipelineEditOp
            {
                Op = c.Op ?? string.Empty,
                StageId = c.StageId,
                After = c.After,
                Before = c.Before,
                From = c.From,
                To = c.To,
                Stage = c.Stage
            }).ToList();
            return (new Proposal(parsed.Summary ?? string.Empty, ops), null);
        }
        catch (JsonException ex)
        {
            return (null, $"The model's proposal wasn't valid: {ex.Message}");
        }
    }

    private sealed record ProposalJson(string? Summary, List<ChangeJson>? Changes);

    private sealed record ChangeJson(string? Op, string? StageId, string? After, string? Before, string? From, string? To, PipelineStagePatch? Stage);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
