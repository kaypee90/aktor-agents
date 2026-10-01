using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentRuntime.Api.Interop;

/// <summary>The budget an MCP caller may ask for; anything above the server's task ceiling is cut down to it.</summary>
public sealed record McpBudget
{
    [Description("Most tokens the whole agent team may use.")]
    public int? MaxTokens { get; init; }
    [Description("Most US dollars the whole agent team may spend.")]
    public decimal? MaxCostUsd { get; init; }
    [Description("Wall-clock limit for the task, in seconds.")]
    public int? MaxDurationSeconds { get; init; }
    [Description("Most agents the root agent may start directly.")]
    public int? MaxChildren { get; init; }
    [Description("Most tool calls the whole team may make.")]
    public int? MaxToolCalls { get; init; }

    public ResourceBudget? ToBudget(ResourceBudget defaults) =>
        MaxTokens is null && MaxCostUsd is null && MaxDurationSeconds is null && MaxChildren is null && MaxToolCalls is null
            ? null
            : defaults with
            {
                MaxTokens = MaxTokens ?? defaults.MaxTokens,
                MaxCostUsd = MaxCostUsd ?? defaults.MaxCostUsd,
                MaxDurationSeconds = MaxDurationSeconds ?? defaults.MaxDurationSeconds,
                MaxChildren = MaxChildren ?? defaults.MaxChildren,
                MaxToolCalls = MaxToolCalls ?? defaults.MaxToolCalls
            };
}

public sealed class McpServerSettings
{
    public const string SectionName = "Mcp";

    /// <summary>Longest a single call may block waiting for a task (run_goal with wait, get_task_status
    /// with wait_seconds). Longer tasks: poll again, or use a callback_url.</summary>
    public int MaxWaitSeconds { get; set; } = 600;
}

/// <summary>
/// Aktor as an MCP server (docs/integrations.md): other agents and automation tools hand
/// open-ended goals to a governed agent team here. Every tool runs as the API key's organization
/// and goes through <see cref="TaskService"/>, so budgets, the task ceiling, plan quotas and
/// tenant isolation apply exactly as they do to POST /api/tasks.
///
/// <para>Parameter names are snake_case on purpose: they are the public tool schema.</para>
/// </summary>
[McpServerToolType]
public sealed class AktorMcpTools(TaskService tasks, IHttpContextAccessor http, Microsoft.Extensions.Options.IOptions<McpServerSettings> settings,
    Microsoft.Extensions.Options.IOptions<Configuration.DefaultBudgetOptions> defaultBudget)
{
    private string TenantId => http.HttpContext!.Caller().TenantId;

    [McpServerTool(Name = "run_goal", Title = "Run a goal with a governed agent team", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Start an autonomous agent team on an open-ended goal. The team plans, spawns specialist agents and works " +
                 "under a budget the server enforces. Returns a task_id at once; then poll get_task_status (it can wait), pass " +
                 "wait=true to block until the task finishes (progress notifications are sent while it runs), or give a " +
                 "callback_url to be POSTed the result. Tasks usually take minutes.")]
    [Authorize(Policy = Policies.Member)]
    public async Task<TaskView> run_goal(
        [Description("What the team should achieve, in plain language.")] string goal,
        [Description("Spending limits for this task. Optional; the server's defaults and ceiling apply.")] McpBudget? budget = null,
        [Description("Optional workspace id: hand the goal to that workspace's coordinator (its connections, safety policy and daily budget apply) instead of starting a new task.")] string? workspace = null,
        [Description("Block until the task finishes (at most the server's wait limit), sending progress notifications.")] bool wait = false,
        [Description("Called with a signed JSON POST when the task finishes (tasks only, not workspace requests).")] string? callback_url = null,
        [Description("Secret for signing the callback: header X-Aktor-Signature: sha256=HMAC_SHA256(secret, body).")] string? callback_secret = null,
        [Description("Your own id for this run, echoed in every result and on the run's events. Generated if omitted.")] string? correlation_id = null,
        [Description("Optional team-shape rules for this task (max_agents, max_fan_out_by_depth, spawner_roles, prevent_duplicate_roles, goal_type…). They add to the server's rules and can only tighten them.")] Safety.TeamPolicy? team_policy = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = await Guard(() => tasks.StartAsync(TenantId, new StartTaskRequest
        {
            Goal = goal,
            Budget = budget?.ToBudget(defaultBudget.Value.ToBudget()),
            WorkspaceId = workspace,
            CallbackUrl = callback_url,
            CallbackSecret = callback_secret,
            CorrelationId = correlation_id,
            Source = "mcp",
            TeamPolicy = team_policy
        }, cancellationToken));

        return wait ? await WaitAsync(started.TaskId, settings.Value.MaxWaitSeconds, progress, cancellationToken) : started;
    }

    [McpServerTool(Name = "get_task_status", Title = "Get a task's status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Status of a task started with run_goal: state is working, completed, failed or canceled. With wait_seconds " +
                 "the call blocks until the task is done or the time is up (long polling), sending progress notifications.")]
    public async Task<TaskView> get_task_status(
        [Description("The task_id run_goal returned.")] string task_id,
        [Description("Wait up to this many seconds for the task to finish (0 = answer now).")] int wait_seconds = 0,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        wait_seconds > 0
            ? await WaitAsync(task_id, wait_seconds, progress, cancellationToken)
            : await tasks.GetAsync(TenantId, task_id, cancellationToken) ?? throw NotFound(task_id);

    [McpServerTool(Name = "get_task_result", Title = "Get a task's final result", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The final result once a task is done: summary, findings from every agent, artifacts, unresolved items and " +
                 "metrics (tokens, cost, tool calls). ready=false while the task is still working.")]
    public async Task<TaskResultView> get_task_result(
        [Description("The task_id run_goal returned.")] string task_id,
        CancellationToken cancellationToken = default) =>
        await tasks.GetResultAsync(TenantId, task_id, cancellationToken) ?? throw NotFound(task_id);

    [McpServerTool(Name = "cancel_task", Title = "Cancel a task", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Stop every agent of a task. Work already done stays in the task's history.")]
    [Authorize(Policy = Policies.Member)]
    public async Task<TaskView> cancel_task(
        [Description("The task_id run_goal returned.")] string task_id,
        CancellationToken cancellationToken = default) =>
        await Guard(() => tasks.CancelAsync(TenantId, task_id, cancellationToken)) ?? throw NotFound(task_id);

    [McpServerTool(Name = "list_agents", Title = "List a task's agents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The agent team working on a task: each agent's id, role, status, parent and depth in the tree.")]
    public async Task<TaskAgentsView> list_agents(
        [Description("The task_id run_goal returned.")] string task_id,
        CancellationToken cancellationToken = default)
    {
        var agents = await tasks.ListAgentsAsync(TenantId, task_id, cancellationToken) ?? throw NotFound(task_id);
        return new TaskAgentsView(task_id, agents);
    }

    private async Task<TaskView> WaitAsync(string taskId, int seconds, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, settings.Value.MaxWaitSeconds));
        Func<TaskProgress, ValueTask>? report = progress is null
            ? null
            : p =>
            {
                progress.Report(new ProgressNotificationValue { Progress = p.Progress, Total = p.Total, Message = p.Message });
                return ValueTask.CompletedTask;
            };
        return await tasks.WaitAsync(TenantId, taskId, timeout, report, ct) ?? throw NotFound(taskId);
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (TaskServiceException ex)
        {
            // Shown to the caller as a tool error (isError), not a protocol failure.
            throw new McpException(ex.Message);
        }
    }

    private static McpException NotFound(string taskId) => new($"No task '{taskId}' in this organization.");
}

public sealed record TaskAgentsView(string TaskId, IReadOnlyList<TaskAgentView> Agents);

public static class McpJson
{
    /// <summary>Snake_case results (task_id, dashboard_url…), matching the REST API.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
}
