using System.Text.Json.Nodes;
using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Solutions: business applications packaged as one unit (docs/solutions.md). Builders install,
/// upgrade and test them here (usually with the <c>aktor</c> CLI and an API key), read and write
/// their records from their own backend, and issue tokens for their end users.
/// </summary>
[ApiController]
[Route("api/solutions")]
public sealed class SolutionsController(SolutionService solutions, IRecordStore records, ISolutionCatalog catalog, SolutionTokens tokens,
    IOptions<SolutionOptions> options, SolutionUsageService usage, SolutionRegistryService registry, SolutionDesignService designer) : ControllerBase
{
    public sealed record DesignBody(JsonObject? Manifest, List<DraftSkill>? Skills, List<DesignTurn>? History, string? Request);
    private IActionResult Problem(SolutionException ex) => StatusCode(ex.Status, new { error = ex.Message, errors = ex.Errors });

    /// <summary>A deploy from the registry: the package's bundle, integrity-checked, in place of an uploaded one.</summary>
    private async Task<(SolutionDeployRequest Request, string? PackageId)> FromRegistryAsync(SolutionDeployRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Package)) return (body, null);
        var (bundle, package) = await registry.ResolveAsync(HttpContext.Caller().TenantId, body.Package, ct);
        return (body with { Bundle = bundle }, package.PackageId);
    }

    /// <summary>Checks a package without installing it: every problem, the warnings, what it would
    /// install, and the inputs and secrets it asks for.</summary>
    [HttpPost("validate")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Validate([FromBody] SolutionBundle bundle, CancellationToken ct) =>
        Ok(await solutions.ValidateAsync(HttpContext.Caller().TenantId, bundle, ct));

    /// <summary>
    /// A package as structured JSON (snake_case, files it includes already inlined), for the
    /// dashboard's solution builder to edit: what aktor.yaml says, without a YAML parser in the
    /// browser. Reading errors are listed; validation is <c>validate</c>'s job.
    /// </summary>
    /// <summary>
    /// The builder's conversation: a request in plain language ("add a Vendor entity and let triage
    /// assign vendors") against the draft as it is now, answered with a reply and the changed draft,
    /// validated. Nothing is saved; the builder applies it if the person wants it.
    /// </summary>
    [HttpPost("design")]
    [Authorize(Policies.Member)]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Design([FromBody] DesignBody body, CancellationToken ct) =>
        Ok(await designer.ProposeAsync(HttpContext.Caller().TenantId, body.Manifest ?? new JsonObject(), body.Skills ?? [], body.History ?? [],
            body.Request ?? string.Empty, ct));

    [HttpPost("parse")]
    [Authorize(Policies.Member)]
    public IActionResult Parse([FromBody] SolutionBundle bundle)
    {
        var (manifest, errors) = SolutionManifestReader.Read(bundle);
        return Ok(new
        {
            manifest = manifest is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(manifest, SolutionManifestReader.Json),
            files = SolutionManifestReader.Files(bundle),
            errors
        });
    }

    /// <summary>The package an install was deployed from, as uploaded (no secrets are ever in it):
    /// to open it in the builder, download it, or deploy it elsewhere.</summary>
    [HttpGet("{id}/package")]
    public async Task<IActionResult> Package(string id, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            return Ok(System.Text.Json.JsonSerializer.Deserialize<SolutionBundle>(row.BundleJson, SolutionManifestReader.Json) ?? new SolutionBundle());
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Installs a package (Admins: it adds connections and their secrets).</summary>
    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Install([FromBody] SolutionDeployRequest body, CancellationToken ct)
    {
        try
        {
            var (request, packageId) = await FromRegistryAsync(body, ct);
            var result = await solutions.InstallAsync(HttpContext.Caller().TenantId, HttpContext.Caller().ActorId, request, ct);
            if (packageId is not null)
            {
                await registry.CountInstallAsync(packageId, ct);
                result["package"] = packageId;
            }

            return Created($"/api/solutions/{result["install_id"]}", result);
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool all, CancellationToken ct)
    {
        var rows = await solutions.ListAsync(HttpContext.Caller().TenantId, all, ct);
        return Ok(rows.Select(r => new
        {
            install_id = r.InstallId,
            name = r.Name,
            title = r.Title,
            version = r.Version,
            status = r.Status,
            installed_at = r.InstalledAt,
            updated_at = r.UpdatedAt
        }));
    }

    /// <summary>One install, by id or by name.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            return Ok(await solutions.ViewAsync(row, HttpContext.Caller().Role >= Tenancy.TenantRole.Admin, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Upgrades an install to a new version of its package (or redeploys with force).
    /// With dry_run, reports what would change.</summary>
    [HttpPut("{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Upgrade(string id, [FromBody] SolutionDeployRequest body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            var (request, packageId) = await FromRegistryAsync(body, ct);
            var result = await solutions.UpgradeAsync(HttpContext.Caller().TenantId, row.InstallId, HttpContext.Caller().ActorId, request, ct);
            if (packageId is not null) result["package"] = packageId;
            return Ok(result);
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Uninstalls: its workspaces are archived. Its records are kept unless purge is set.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Uninstall(string id, [FromQuery] bool purge, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            await solutions.UninstallAsync(HttpContext.Caller().TenantId, row.InstallId, purge, HttpContext.Caller().ActorId, ct);
            return NoContent();
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    public sealed record EvalsBody(List<string>? Names);

    /// <summary>Runs the solution's evals for real and reports each one (this takes as long as the runs do).</summary>
    [HttpPost("{id}/evals")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> Evals(string id, [FromBody] EvalsBody? body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            return Ok(await solutions.RunEvalsAsync(HttpContext.Caller().TenantId, row.InstallId, body?.Names, HttpContext.Caller().ActorId, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>
    /// What the solution cost, by end user (their conversations), workspace or day, from the
    /// runtime's own metering: for passing costs on to your customers. Defaults to this calendar
    /// month; <c>?format=csv</c> for a spreadsheet.
    /// </summary>
    [HttpGet("{id}/usage")]
    public async Task<IActionResult> Usage(string id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? by,
        [FromQuery] string? format, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            var report = await usage.GetAsync(row, from, to, by ?? "end_user", ct);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return File(System.Text.Encoding.UTF8.GetBytes(SolutionUsageService.ToCsv(report)), "text/csv",
                    $"{row.Name}-usage-{report.By}-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
            }

            return Ok(report);
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    public sealed record PublishBody(string? Visibility);

    /// <summary>Publishes the installed version's package to the registry (Admins), as uploaded.</summary>
    [HttpPost("{id}/publish")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Publish(string id, [FromBody] PublishBody? body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            var bundle = System.Text.Json.JsonSerializer.Deserialize<SolutionBundle>(row.BundleJson, SolutionManifestReader.Json) ?? new SolutionBundle();
            return StatusCode(201, await registry.PublishAsync(row.TenantId, HttpContext.Caller().ActorId, bundle, body?.Visibility ?? "organization", ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    public sealed record TokenBody(string UserId, string? Name, int? TtlMinutes);

    /// <summary>
    /// A token for one of the solution's end users, for the embed widget or the apps API. Call it
    /// from your backend (with an API key) after your own sign-in: the user id is yours, and
    /// everything the token reaches is that user's alone.
    /// </summary>
    [HttpPost("{id}/end-user-tokens")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> EndUserToken(string id, [FromBody] TokenBody body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            if (row.Status != nameof(SolutionInstallStatus.Active)) return Conflict(new { error = "The solution isn't active." });
            var user = body.UserId?.Trim() ?? string.Empty;
            if (user.Length is 0 or > 200 || user.Contains(':')) return BadRequest(new { error = "user_id is required: your own id for the user, up to 200 characters, without ':'." });
            var max = options.Value.MaxEndUserTokenLifetime;
            var ttl = TimeSpan.FromMinutes(Math.Clamp(body.TtlMinutes ?? 60, 5, (int)max.TotalMinutes));
            var token = tokens.IssueEndUserToken(row.TenantId, row.InstallId, user, string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim()[..Math.Min(100, body.Name.Trim().Length)], ttl);
            return Ok(new { token, install_id = row.InstallId, user_id = user, expires_at = DateTimeOffset.UtcNow.Add(ttl) });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    // ---------------------------------------------------------------- records (the builder's backend)

    private async Task<(InstalledSolution Solution, EntityDefinition Definition)?> EntityAsync(string id, string entity, CancellationToken ct, bool write = false)
    {
        var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
        var solution = await catalog.GetAsync(row.TenantId, row.InstallId, ct);
        if (solution is null || !solution.Manifest.Entities.TryGetValue(entity, out var definition)) return null;
        // An uninstalled solution's records can still be read (and exported), not changed: nothing
        // would react to the change.
        if (write && solution.Status != SolutionInstallStatus.Active) throw new SolutionException("The solution isn't installed any more; its records are read-only.", StatusCodes.Status409Conflict);
        return (solution, definition);
    }

    private string Writer => "api:" + HttpContext.Caller().ActorId;

    /// <summary>
    /// Lists records. Exact matches as <c>?where.status=open</c>, comparisons as
    /// <c>?filter=ends_on&lt;=today+30d</c> (repeatable; ops &lt; &lt;= &gt; &gt;= == != ~ for contains),
    /// plus order_by, desc, limit and offset. <c>?end_user=</c> narrows to one end user's records.
    /// </summary>
    [HttpGet("{id}/records/{entity}")]
    public async Task<IActionResult> ListRecords(string id, string entity, [FromQuery(Name = "order_by")] string? orderBy, [FromQuery] bool? desc,
        [FromQuery] int? limit, [FromQuery] int? offset, [FromQuery(Name = "end_user")] string? endUser, CancellationToken ct)
    {
        try
        {
            if (await EntityAsync(id, entity, ct) is not { } e) return NotFound(new { error = $"No entity '{entity}'." });
            var where = Request.Query.Where(q => q.Key.StartsWith("where.", StringComparison.Ordinal)).ToDictionary(q => q.Key[6..], q => q.Value.ToString());
            var filters = new List<WatchCondition>();
            foreach (var f in Request.Query["filter"])
            {
                if (ParseFilter(f) is not { } condition) return BadRequest(new { error = $"Can't read the filter '{f}' (e.g. rent>=1000, ends_on<=today+30d, name~smith)." });
                filters.Add(condition);
            }

            var (items, total) = await records.ListAsync(new RecordScope(e.Solution.TenantId, e.Solution.InstallId, endUser), entity, e.Definition, new RecordQuery
            {
                Where = where,
                Filters = filters,
                OrderBy = orderBy,
                Descending = desc ?? true,
                Limit = limit ?? 50,
                Offset = offset ?? 0
            }, ct);
            return Ok(new { entity, total, items = items.Select(r => r.ToJson()) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    private static WatchCondition? ParseFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var op in new[] { "<=", ">=", "!=", "==", "<", ">", "~", "=" })
        {
            var at = text.IndexOf(op, StringComparison.Ordinal);
            if (at <= 0) continue;
            var field = text[..at].Trim();
            var value = text[(at + op.Length)..].Trim();
            return new WatchCondition { Field = field, Op = op switch { "~" => "contains", "=" => "==", _ => op }, Value = value };
        }

        return text.EndsWith('?') ? new WatchCondition { Field = text.TrimEnd('?').Trim(), Op = "exists" } : null;
    }

    [HttpGet("{id}/records/{entity}/{recordId}")]
    public async Task<IActionResult> GetRecord(string id, string entity, string recordId, CancellationToken ct)
    {
        try
        {
            if (await EntityAsync(id, entity, ct) is not { } e) return NotFound(new { error = $"No entity '{entity}'." });
            return await records.GetAsync(new RecordScope(e.Solution.TenantId, e.Solution.InstallId), entity, e.Definition, recordId, ct) is { } record
                ? Ok(record.ToJson())
                : NotFound(new { error = $"No {entity} '{recordId}'." });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    private IActionResult Written(RecordWriteResult result, bool created = false) =>
        result.Success ? created && !result.Replayed ? StatusCode(201, result.Record!.ToJson()) : Ok(result.Record!.ToJson())
        : result.NotFound ? NotFound(new { error = result.Errors.FirstOrDefault(), errors = result.Errors })
        : result.Conflict ? Conflict(new { error = result.Errors.FirstOrDefault(), errors = result.Errors })
        : UnprocessableEntity(new { error = string.Join(" ", result.Errors), errors = result.Errors });

    /// <summary>Creates a record (the body is its fields). An Idempotency-Key header makes a retried
    /// create return the record the first one made.</summary>
    [HttpPost("{id}/records/{entity}")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> CreateRecord(string id, string entity, [FromBody] JsonObject fields, CancellationToken ct)
    {
        try
        {
            if (await EntityAsync(id, entity, ct, write: true) is not { } e) return NotFound(new { error = $"No entity '{entity}'." });
            var key = Request.Headers["Idempotency-Key"].ToString();
            var recordId = string.IsNullOrWhiteSpace(key) ? null : SolutionIds.RecordFor($"{e.Solution.InstallId}:api:{HttpContext.Caller().TenantId}:{key.Trim()}");
            return Written(await records.CreateAsync(new RecordScope(e.Solution.TenantId, e.Solution.InstallId), e.Solution.Manifest, entity, fields,
                new RecordWriter(Writer), recordId, ct), created: true);
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Changes fields (null clears one). <c>If-Match: &lt;version&gt;</c> refuses the change
    /// if the record changed since that version.</summary>
    [HttpPatch("{id}/records/{entity}/{recordId}")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> UpdateRecord(string id, string entity, string recordId, [FromBody] JsonObject fields, CancellationToken ct)
    {
        try
        {
            if (await EntityAsync(id, entity, ct, write: true) is not { } e) return NotFound(new { error = $"No entity '{entity}'." });
            int? expected = int.TryParse(Request.Headers.IfMatch.ToString().Trim('"', ' ', 'W', '/'), out var v) ? v : null;
            return Written(await records.UpdateAsync(new RecordScope(e.Solution.TenantId, e.Solution.InstallId), e.Solution.Manifest, entity, recordId, fields,
                new RecordWriter(Writer), expected, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    [HttpDelete("{id}/records/{entity}/{recordId}")]
    [Authorize(Policies.Member)]
    public async Task<IActionResult> DeleteRecord(string id, string entity, string recordId, CancellationToken ct)
    {
        try
        {
            if (await EntityAsync(id, entity, ct, write: true) is not { } e) return NotFound(new { error = $"No entity '{entity}'." });
            var result = await records.DeleteAsync(new RecordScope(e.Solution.TenantId, e.Solution.InstallId), e.Solution.Manifest, entity, recordId, new RecordWriter(Writer), ct);
            return result.Success ? NoContent() : Written(result);
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }
}
