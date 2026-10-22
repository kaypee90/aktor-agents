using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Pipelines;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// The apps API (docs/solutions.md#the-apps-api): how an application reaches one installed solution.
/// It takes two credentials, and nothing else:
/// <list type="bullet">
/// <item>an <b>end-user token</b> (<c>Bearer aeu_…</c>, or the builder's own sign-in JWT), from the
/// embed widget or the builder's front end: the assistant, and that user's own conversations and records;</item>
/// <item>an <b>app key</b> (<c>Bearer aks_…</c>), from the builder's backend only: issue end-user tokens,
/// read and write the solution's records, and start and follow its workflows.</item>
/// </list>
/// It's open to any origin (the credential is the token, there are no cookies), and neither
/// credential reaches anything outside its one solution. An app key sent from a web page is refused:
/// it belongs on a server.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableCors(AppsCors.Policy)]
[Route("api/apps/{installId}")]
public sealed class AppsController(SolutionTokens tokens, SolutionAppService apps, EndUserJwtVerifier jwts, ISolutionCatalog catalog,
    SolutionAppKeyService appKeys, SolutionRecordsApi recordsApi, IGrainFactory grains) : ControllerBase
{
    /// <summary>Runs one app key may start per minute, across the cluster: a loop in the builder's
    /// code stops here instead of in the bill.</summary>
    public const int RunsPerMinutePerKey = 60;
    private const int MaxWaitSeconds = 60;

    private string? Bearer
    {
        get
        {
            var header = Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        }
    }

    private bool HasAppKey => AppKeyFormat.LooksLike(Bearer);

    /// <summary>An Aktor end-user token, or (for a solution that accepts them) the builder's own
    /// sign-in JWT, checked against the builder's published keys.</summary>
    private async Task<SolutionAppService.EndUser?> UserAsync(string installId, CancellationToken ct)
    {
        var token = Bearer;
        var claims = tokens.VerifyEndUserToken(token);
        if (claims is null && EndUserJwtVerifier.LooksLikeJwt(token) && await catalog.FindAsync(installId, ct) is { Manifest.EndUsers.Jwt: not null } solution)
        {
            claims = await jwts.VerifyAsync(token!, solution, ct);
        }

        if (claims is null) return null;
        return await apps.ResolveAsync(claims, installId, Request.Headers.Origin.ToString(), ct);
    }

    private async Task<IActionResult> As(string installId, Func<SolutionAppService.EndUser, Task<object>> action, CancellationToken ct)
    {
        try
        {
            if (HasAppKey) return StatusCode(403, new { error = "This is for end users: send an end-user token, not an app key." });
            if (await UserAsync(installId, ct) is not { } user) return Unauthorized(new { error = "An end-user token is required (Authorization: Bearer aeu_…), and it may have expired." });
            return Ok(await action(user));
        }
        catch (SolutionException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>For the builder's backend: the app key must be valid for this install, and the request
    /// must not come from a web page (a browser always sends Origin on these requests).</summary>
    private async Task<IActionResult> AsApp(string installId, Func<SolutionAppKeyService.AppCaller, Task<IActionResult>> action, CancellationToken ct)
    {
        try
        {
            if (!HasAppKey) return Unauthorized(new { error = "An app key is required (Authorization: Bearer aks_…). Create one under the solution's Integrate tab." });
            if (Request.Headers.Origin.ToString() is { Length: > 0 })
            {
                return StatusCode(403, new { error = "App keys are for your server. Never put one in a web page: have your backend issue end-user tokens instead." });
            }

            if (await appKeys.ResolveAsync(Bearer, installId, ct) is not { } app) return Unauthorized(new { error = "This app key isn't valid for this solution, or it was revoked." });
            return await action(app);
        }
        catch (SolutionException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message, errors = ex.Errors });
        }
    }

    // ---------------------------------------------------------------- the app, either credential

    /// <summary>With an end-user token: the app, the assistant and the user. With an app key: what
    /// the backend can use (its workflows, entities and assistant), to check an integration.</summary>
    [HttpGet]
    public Task<IActionResult> Describe(string installId, CancellationToken ct) =>
        HasAppKey
            ? AsApp(installId, app => Task.FromResult<IActionResult>(Ok(new
            {
                app = app.Solution.Manifest.DisplayTitle,
                install_id = app.InstallId,
                version = app.Solution.Version,
                key = new { id = app.KeyId, name = app.KeyName },
                workflows = app.Solution.Workspaces.Keys.Order(),
                entities = app.Solution.Manifest.Entities.Keys.Order(),
                assistant = app.Solution.Manifest.Assistant is { } a ? new { name = a.Name, allowed_origins = a.AllowedOrigins } : null
            })), ct)
            : As(installId, u => Task.FromResult(apps.Describe(u)), ct);

    // ---------------------------------------------------------------- end users

    public sealed record MessageBody(string Message);

    [HttpGet("conversations")]
    public Task<IActionResult> Conversations(string installId, CancellationToken ct) => As(installId, u => apps.ListAsync(u, ct), ct);

    /// <summary>Starts a conversation with the assistant; poll it for the reply.</summary>
    [HttpPost("conversations")]
    public Task<IActionResult> Start(string installId, [FromBody] MessageBody body, CancellationToken ct) =>
        As(installId, u => apps.StartAsync(u, body.Message, ct), ct);

    [HttpGet("conversations/{conversationId}")]
    public Task<IActionResult> Conversation(string installId, string conversationId, CancellationToken ct) =>
        As(installId, u => apps.GetAsync(u, conversationId, ct), ct);

    [HttpPost("conversations/{conversationId}/messages")]
    public Task<IActionResult> Send(string installId, string conversationId, [FromBody] MessageBody body, CancellationToken ct) =>
        As(installId, u => apps.SendAsync(u, conversationId, body.Message, ct), ct);

    // ---------------------------------------------------------------- the builder's backend (app key)

    /// <summary>A token for one of your users, after your own sign-in: what the embed widget's
    /// <c>data-token-url</c> returns.</summary>
    [HttpPost("end-user-tokens")]
    public Task<IActionResult> EndUserToken(string installId, [FromBody] SolutionAppService.TokenBody body, CancellationToken ct) =>
        AsApp(installId, app => Task.FromResult<IActionResult>(Ok(apps.IssueToken(app.TenantId, app.InstallId, body))), ct);

    /// <summary>With an end-user token, the user's own records of an entity that belongs to people.
    /// With an app key, any of the solution's records, with the records API's filters.</summary>
    [HttpGet("records/{entity}")]
    public Task<IActionResult> Records(string installId, string entity, [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct) =>
        HasAppKey
            ? AsApp(installId, app => recordsApi.ListAsync(app.Solution, entity, Request.Query, ct), ct)
            : As(installId, u => apps.RecordsAsync(u, entity, limit ?? 50, offset ?? 0, ct), ct);

    [HttpGet("records/{entity}/{recordId}")]
    public Task<IActionResult> Record(string installId, string entity, string recordId, CancellationToken ct) =>
        AsApp(installId, app => recordsApi.GetAsync(app.Solution, entity, recordId, ct), ct);

    /// <summary>Creates a record (the body is its fields). An Idempotency-Key header makes a retried
    /// create return the record the first one made.</summary>
    [HttpPost("records/{entity}")]
    public Task<IActionResult> CreateRecord(string installId, string entity, [FromBody] JsonObject fields, CancellationToken ct) =>
        AsApp(installId, app => recordsApi.CreateAsync(app.Solution, entity, fields, Request.Headers["Idempotency-Key"].ToString(), "app", app.Actor, ct), ct);

    /// <summary>Changes fields (null clears one). <c>If-Match: &lt;version&gt;</c> refuses the change
    /// if the record changed since that version.</summary>
    [HttpPatch("records/{entity}/{recordId}")]
    public Task<IActionResult> UpdateRecord(string installId, string entity, string recordId, [FromBody] JsonObject fields, CancellationToken ct) =>
        AsApp(installId, app => recordsApi.UpdateAsync(app.Solution, entity, recordId, fields, Request.Headers.IfMatch.ToString(), app.Actor, ct), ct);

    [HttpDelete("records/{entity}/{recordId}")]
    public Task<IActionResult> DeleteRecord(string installId, string entity, string recordId, CancellationToken ct) =>
        AsApp(installId, app => recordsApi.DeleteAsync(app.Solution, entity, recordId, app.Actor, ct), ct);

    public sealed record RunBody(string? Workflow, string? Input, JsonElement? Data);

    /// <summary>
    /// Runs one of the solution's workflows with an input (text, data, or both), under the same
    /// budgets, safety policy and approvals as any other run. An Idempotency-Key header makes a
    /// retried request return the first request's run. Follow it with <c>GET runs/{run_id}?wait=30</c>.
    /// </summary>
    [HttpPost("runs")]
    public Task<IActionResult> StartRun(string installId, [FromBody] RunBody body, CancellationToken ct) =>
        AsApp(installId, async app =>
        {
            var key = body.Workflow?.Trim() ?? string.Empty;
            if (!app.Solution.Workspaces.TryGetValue(key, out var workspaceId))
            {
                return NotFound(new { error = $"No workflow '{key}'. This solution has: {string.Join(", ", app.Solution.Workspaces.Keys.Order())}." });
            }

            var data = body.Data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } d ? d.GetRawText() : null;
            var input = string.IsNullOrWhiteSpace(body.Input) ? data : body.Input;
            if (string.IsNullOrWhiteSpace(input)) return BadRequest(new { error = "Give the run an input, data, or both." });
            var requestId = Request.Headers["Idempotency-Key"].ToString().Trim() is { Length: > 0 and <= 200 } k ? k : null;

            // A retried request returns its run without spending a slot of the rate limit.
            if (requestId is not null && await grains.GetGrain<IPipelineRunGrain>(PipelineIds.RunFor(PipelineIds.AppRequestEvent(workspaceId, requestId))).GetView() is { } prior)
            {
                return Accepted(new { run_id = prior.RunId, workflow = key, number = prior.Number, status = "already started" });
            }

            if (!await grains.GetGrain<IEndUserThrottleGrain>($"{app.InstallId}:{app.Actor}").TryAcquire(RunsPerMinutePerKey, TimeSpan.FromMinutes(1)))
            {
                return StatusCode(429, new { error = $"This app key started {RunsPerMinutePerKey} runs in the last minute; slow down." });
            }

            var started = await grains.GetGrain<IWorkspaceGrain>(workspaceId).StartRunFromApp(input, data, app.Actor, requestId);
            if (!started.Success || started.RunId is null) return Conflict(new { error = started.Message });
            return Accepted(new { run_id = started.RunId, workflow = key, number = started.Number, status = started.Message });
        }, ct);

    /// <summary>A run of one of the solution's workflows. <c>?wait=N</c> (up to 60) holds the request
    /// until the run finishes or N seconds pass, so a backend needn't poll quickly.</summary>
    [HttpGet("runs/{runId}")]
    public Task<IActionResult> Run(string installId, string runId, [FromQuery] int? wait, CancellationToken ct) =>
        AsApp(installId, async app =>
        {
            if (!PipelineIds.IsRun(runId)) return NotFound(new { error = $"No run '{runId}'." });
            var run = grains.GetGrain<IPipelineRunGrain>(runId);
            var view = await run.GetView();
            var workflow = app.Solution.Workspaces.FirstOrDefault(w => w.Value == view?.WorkspaceId).Key;
            if (view is null || workflow is null) return NotFound(new { error = $"No run '{runId}'." });

            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait ?? 0, 0, MaxWaitSeconds));
            while (view is { CompletedAt: null } && DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                view = await run.GetView() ?? view;
            }

            return Ok(RunView(view!, workflow));
        }, ct);

    /// <summary>What a backend needs from a run: whether it's done, how it ended, its summary, and
    /// each stage's result (an action or script stage's output as JSON).</summary>
    private static object RunView(PipelineRunView view, string workflow) => new
    {
        run_id = view.RunId,
        workflow,
        number = view.Number,
        status = view.Status,
        done = view.CompletedAt is not null,
        paused = view.Paused,
        summary = view.Summary,
        created_at = view.CreatedAt,
        started_at = view.StartedAt,
        completed_at = view.CompletedAt,
        stages = view.Stages.Select(s => new
        {
            id = s.StageId,
            name = s.Name,
            kind = s.Kind,
            status = s.Status,
            summary = s.Summary,
            output = s.OutputJson is { Length: > 0 } json ? ParseJson(json) : null,
            error = s.Error,
            artifacts = s.Artifacts
        })
    };

    private static JsonNode? ParseJson(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}

public static class AppsCors
{
    public const string Policy = "apps";
}
