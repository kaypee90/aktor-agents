using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Workspaces;

public sealed record PlannedPart(string Title, string Size, bool DependsOnOthers);

public sealed record WorkPlan(string Approach, int Workers, IReadOnlyList<string> WorkerParts, IReadOnlyList<string> YourParts, string Guidance);

/// <summary>
/// Turns an agent's breakdown of a request into a recommendation: do it yourself, or give each
/// substantial, independent part to a worker. The model is good at listing the parts of a request
/// and judging their size; it's much less reliable at deciding from that whether splitting pays
/// off. That decision is a simple rule, so the runtime makes it the same way every time.
/// </summary>
public static class WorkPlanner
{
    public static WorkPlan Recommend(IReadOnlyList<PlannedPart> parts, int maxWorkers)
    {
        static bool Substantial(PlannedPart p) => !p.Size.Equals("small", StringComparison.OrdinalIgnoreCase);
        var parallel = parts.Where(p => Substantial(p) && !p.DependsOnOthers).ToList();
        var yours = parts.Except(parallel).Select(p => p.Title).ToList();

        if (parallel.Count >= 2 && maxWorkers > 0)
        {
            var workers = Math.Min(parallel.Count, maxWorkers);
            var grouping = parallel.Count > workers
                ? $" That's more parts than the {workers} workers one request can have, so group them: each worker takes several parts."
                : string.Empty;
            return new WorkPlan("split", workers, parallel.Select(p => p.Title).ToList(), yours,
                $"Split: start {workers} worker{(workers == 1 ? "" : "s")} (spawn_agent, standing=false), one per part: " +
                $"{string.Join("; ", parallel.Select(p => p.Title))}.{grouping} Give each a goal naming exactly its part and the file to " +
                "save it to, then call wait_for_events. " +
                (yours.Count > 0 ? $"Do the rest yourself: {string.Join("; ", yours)}. " : string.Empty) +
                "When they have all reported, combine their results into one deliverable, save it, and tell the user.");
        }

        if (parallel.Count == 1 && parallel[0].Size.Equals("large", StringComparison.OrdinalIgnoreCase) && maxWorkers > 0)
        {
            return new WorkPlan("delegate", 1, [parallel[0].Title], yours,
                $"Delegate the large part to one worker ({parallel[0].Title}), so you stay free to answer the user while it runs. " +
                (yours.Count > 0 ? $"Do the rest yourself: {string.Join("; ", yours)}. " : string.Empty) +
                "Tell the user what's under way, then call wait_for_events.");
        }

        return new WorkPlan("self", 0, [], parts.Select(p => p.Title).ToList(),
            "Do this yourself, step by step: the parts are small or depend on each other, so another agent would cost " +
            "more than it saves.");
    }
}

/// <summary>
/// plan_request: the agent lists a request's parts; the runtime answers with the plan, and the
/// number of workers in it becomes how many the agent may start for this request.
/// </summary>
public sealed class PlanRequestTool(IOptions<WorkspaceOptions> options) : ITool
{
    public const string Name = "plan_request";

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name,
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Break a request into its parts before doing it. For each part give its size (small: a few steps; " +
                      "medium: a solid piece of work; large: extensive research or a long document) and whether it needs " +
                      "another part's result first. Returns whether to do it yourself or hand parts to workers, and how " +
                      "many. Workers can only be started through a plan.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "parts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "What this part produces, e.g. 'Research Nigeria's government'" },
                  "size": { "type": "string", "enum": ["small", "medium", "large"] },
                  "depends_on_other_parts": { "type": "boolean", "description": "True if it needs another part's result first" }
                },
                "required": ["title", "size"]
              }
            }
          },
          "required": ["parts"]
        }
        """
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<PlanArgs>(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson, ToolJson.Options);
        var parts = (args?.Parts ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Select(p => new PlannedPart(p.Title!.Trim(), p.Size ?? "medium", p.DependsOnOtherParts ?? false))
            .ToList();
        if (parts.Count == 0)
        {
            return Task.FromResult(ToolExecutionResult.Fail("plan_request needs parts: list what the request consists of, with a title and size for each."));
        }

        var plan = WorkPlanner.Recommend(parts, options.Value.MaxSpawnsPerRequest);
        return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            approach = plan.Approach,
            workers = plan.Workers,
            worker_parts = plan.WorkerParts,
            your_parts = plan.YourParts,
            guidance = plan.Guidance
        }, ToolJson.Options)));
    }

    private sealed record PlanArgs(List<PartArgs>? Parts);
    private sealed record PartArgs(string? Title, string? Size, bool? DependsOnOtherParts);
}
