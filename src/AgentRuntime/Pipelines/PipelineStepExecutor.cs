using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Integrations;
using AgentRuntime.Safety;
using AgentRuntime.Tools;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Pipelines;

/// <summary>
/// Does an action or script stage's step: no model involved, so the result depends only on the
/// run's data. Actions call a connection's tool under the same rules agents are held to (the
/// organization's and workspace's safety policies, the audit log, idempotency keys); scripts run
/// in the <see cref="IScriptRunner"/> sandbox.
/// </summary>
public sealed class PipelineStepExecutor(
    IGrainFactory grains,
    IntegrationService integrations,
    IScriptRunner scripts,
    IAuditLog audit,
    IEventPublisher events,
    IOptions<PipelineOptions> pipelineOptions,
    IOptions<ScriptOptions> scriptOptions,
    ILogger<PipelineStepExecutor> logger)
{
    private readonly PipelineOptions _opts = pipelineOptions.Value;
    private readonly ScriptOptions _scripts = scriptOptions.Value;

    public async Task<StepResult> ExecuteAsync(StepLaunch launch, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = launch.Stage.Kind switch
            {
                StageKind.Action => await RunActionAsync(launch, ct),
                StageKind.Script => await RunScriptAsync(launch, ct),
                _ => StepResult.Fail("An agent stage isn't a step.", retryable: false)
            };
            return result with { DurationMs = watch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StepResult.Fail("The step was stopped.", retryable: false, ms: watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return StepResult.Fail(ex.Message, retryable: false, ms: watch.ElapsedMilliseconds);
        }
    }

    // ---- Scripts --------------------------------------------------------------------

    /// <summary>Runs <paramref name="code"/> with <paramref name="dataJson"/>, e.g. to try a script out
    /// before saving it.</summary>
    public Task<ScriptRunResult> TestScriptAsync(string language, string code, string dataJson, int? timeoutSeconds, CancellationToken ct) =>
        scripts.RunAsync(new ScriptRunRequest
        {
            Language = language,
            Code = code,
            DataJson = dataJson,
            TimeoutSeconds = Math.Clamp(timeoutSeconds ?? _scripts.DefaultTimeoutSeconds, 1, _scripts.MaxTimeoutSeconds)
        }, ct);

    private async Task<StepResult> RunScriptAsync(StepLaunch launch, CancellationToken ct)
    {
        var stage = launch.Stage;
        var result = await TestScriptAsync(stage.Language ?? ScriptLanguages.Python, stage.Code ?? string.Empty, launch.DataJson, stage.TimeoutSeconds, ct);
        if (!result.Ok) return StepResult.Fail(result.Error ?? "The script failed.", result.Logs);
        if (result.OutputJson.Length > _opts.MaxStageOutputChars)
        {
            return StepResult.Fail($"The script returned {result.OutputJson.Length:N0} characters of JSON; at most {_opts.MaxStageOutputChars:N0} are handed on. Return less (e.g. only the fields later stages need).",
                result.Logs, retryable: false);
        }

        return StepResult.Ok(result.OutputJson, result.Logs, result.DurationMs);
    }

    // ---- Actions --------------------------------------------------------------------

    private async Task<StepResult> RunActionAsync(StepLaunch launch, CancellationToken ct)
    {
        var stage = launch.Stage;
        var toolName = stage.Tool ?? string.Empty;
        var workspace = grains.GetGrain<IWorkspaceGrain>(launch.WorkspaceId);
        var target = await workspace.ResolveConnectionTool(toolName);
        if (target is null)
        {
            return StepResult.Fail($"There's no enabled connection tool '{toolName}': its connection was removed or the tool switched off.", retryable: false);
        }

        // The same policies agents and the MCP gateway are held to.
        var orgPolicy = await grains.GetGrain<Tenancy.ITenantGrain>(Tenancy.TenantIds.Normalize(launch.TenantId)).GetSafetyPolicy();
        var verdict = PolicyEngine.Evaluate(orgPolicy, await workspace.GetSafetyPolicy(), toolName, target.SideEffects);
        if (verdict.Decision == PolicyDecisionKind.Deny)
        {
            await AuditAsync(launch, toolName, target.SideEffects, -1, "denied", $"Blocked by {verdict.Reason}.", "{}", null);
            return StepResult.Fail($"Blocked by {verdict.Reason}.", retryable: false);
        }

        if (verdict.Decision == PolicyDecisionKind.RequireApproval)
        {
            await AuditAsync(launch, toolName, target.SideEffects, -1, "denied", $"{verdict.Reason} requires approval.", "{}", null);
            return StepResult.Fail($"{verdict.Reason} requires a person's approval, and action stages can't wait for one. " +
                                   "Use an agent stage for this step, or change the rule on the Safety tab.", retryable: false);
        }

        var data = JsonNode.Parse(launch.DataJson) as JsonObject ?? [];
        List<JsonNode?>? items = null;
        if (!string.IsNullOrWhiteSpace(stage.ForEach))
        {
            items = PipelineExpressions.Evaluate(stage.ForEach, data) switch
            {
                null => [],
                JsonArray a => [.. a],
                var single => [single]
            };
            if (items.Count > _opts.MaxForEachItems)
            {
                return StepResult.Fail($"'{stage.ForEach}' has {items.Count} items; an action handles at most {_opts.MaxForEachItems} per run.", retryable: false);
            }
        }

        var outputs = new JsonArray();
        var count = items?.Count ?? 1;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var context = (JsonObject)data.DeepClone();
            if (items is not null)
            {
                context["item"] = items[i]?.DeepClone();
                context["index"] = i;
            }

            var arguments = PipelineExpressions.Render(stage.Arguments ?? "{}", context).ToJsonString();
            await PublishAsync(launch, RuntimeEventType.AgentToolCalled, $"'{stage.Name}' is calling {toolName}" + (items is null ? "." : $" (item {i + 1} of {count})."),
                new Dictionary<string, string> { ["tool"] = toolName, ["arguments"] = arguments });

            var result = await integrations.ExecuteToolAsync(launch.WorkspaceId, target, new ToolExecutionRequest
            {
                ToolName = toolName,
                AgentId = launch.StepId,
                TaskId = launch.RunId,
                TenantId = Tenancy.TenantIds.Normalize(launch.TenantId),
                WorkspaceId = launch.WorkspaceId,
                ArgumentsJson = arguments,
                // Fixed by the run, stage and item (not the attempt), so a retried call can't take effect twice.
                IdempotencyKey = $"{launch.RunId}:{stage.StageId}:{i}",
                GrantedPermissions = ToolPermission.Integrations,
                CancellationToken = ct
            });

            await grains.GetGrain<Tenancy.ITenantGrain>(Tenancy.TenantIds.Normalize(launch.TenantId)).RecordUsage(new Tenancy.UsageDelta { ToolCalls = 1 });
            await AuditAsync(launch, toolName, target.SideEffects, i, result.Success ? "ok" : "failed",
                result.Success ? "ok" : result.ErrorMessage ?? "failed", arguments, result.Success ? result.ResultJson : null);
            await PublishAsync(launch, RuntimeEventType.AgentToolCompleted, $"{toolName} {(result.Success ? "completed" : "failed")} for '{stage.Name}'.",
                new Dictionary<string, string>
                {
                    ["tool"] = toolName,
                    ["arguments"] = arguments,
                    ["success"] = result.Success.ToString(),
                    ["error"] = result.ErrorMessage ?? string.Empty,
                    ["result"] = result.Success ? result.ResultJson : string.Empty
                });

            if (!result.Success)
            {
                var where = items is null ? "" : $"Item {i + 1} of {count}: ";
                // A clean failure of the first call changed nothing, so it can be tried again;
                // after earlier items went through, only calls that are safe to repeat can.
                var retryable = target.SideEffects != ToolSideEffects.NonIdempotent || i == 0;
                return StepResult.Fail(where + (result.ErrorMessage ?? "The call failed."), retryable: retryable);
            }

            outputs.Add(ParseLenient(result.ResultJson));
        }

        var output = items is null ? outputs[0]?.DeepClone() : outputs;
        var json = output?.ToJsonString() ?? "null";
        if (json.Length > _opts.MaxStageOutputChars)
        {
            logger.LogInformation("Run {RunId}: action {Stage} output truncated from {Length} characters", launch.RunId, stage.StageId, json.Length);
            json = JsonSerializer.Serialize(new { truncated = true, preview = json[.._opts.MaxStageOutputChars] });
        }

        return StepResult.Ok(json, null, 0);
    }

    private static JsonNode? ParseLenient(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }

    private async Task AuditAsync(StepLaunch launch, string tool, ToolSideEffects sideEffects, int item, string outcome, string message, string arguments, string? result)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Scope = launch.WorkspaceId,
                Key = $"{launch.StepId}:{item}:{outcome}",
                ActorType = "pipeline",
                ActorId = launch.RunId,
                ActorName = $"Run #{launch.RunNumber} · {launch.Stage.Name}",
                Action = outcome == "denied" ? "tool.denied" : "tool.call",
                Target = tool,
                SideEffects = sideEffects.ToString(),
                Outcome = outcome,
                Summary = $"Action '{launch.Stage.Name}' (run #{launch.RunNumber}) called {tool}: {Clip(message, 160)}",
                DetailJson = JsonSerializer.Serialize(new
                {
                    stage_id = launch.Stage.StageId,
                    item = item < 0 ? (int?)null : item,
                    arguments = Clip(arguments, 2000),
                    result = result is null ? null : Clip(result, 500),
                    error = outcome == "ok" ? null : Clip(message, 500)
                })
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't audit action {Stage} of run {RunId}", launch.Stage.StageId, launch.RunId);
        }
    }

    private Task PublishAsync(StepLaunch launch, RuntimeEventType type, string summary, Dictionary<string, string> data)
    {
        data["stage_id"] = launch.Stage.StageId;
        return events.PublishAsync(new RuntimeEvent
        {
            Type = type,
            AgentId = launch.RunId,
            TaskId = launch.RunId,
            TenantId = launch.TenantId,
            CorrelationId = launch.RunId,
            Summary = summary,
            Data = data
        }).AsTask();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
