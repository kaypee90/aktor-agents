using System.Text.Json.Nodes;
using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Solutions: business applications packaged as one unit (docs/solutions.md). Builders install,
/// upgrade and test them here (usually with the <c>aktor</c> CLI and an API key), read and write
/// their records from their own backend, and issue tokens for their end users.
/// </summary>
[ApiController]
[Route("api/solutions")]
public sealed class SolutionsController(SolutionService solutions, ISolutionCatalog catalog, SolutionAppService apps, SolutionUsageService usage, SolutionRegistryService registry, SolutionDesignService designer,
    SolutionAppKeyService appKeys, SolutionRecordsApi recordsApi, SolutionTemplates templates) : ControllerBase
{
    /// <summary>Ready-made solutions to start from in the builder.</summary>
    [HttpGet("templates")]
    public IActionResult Templates() => Ok(templates.List());

    /// <summary>A template's package (manifest and files), to open in the builder as a new draft.</summary>
    [HttpGet("templates/{name}")]
    public IActionResult Template(string name) =>
        templates.Get(name) is { } bundle ? Ok(bundle) : NotFound(new { error = $"No template '{name}'." });

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

    /// <summary>
    /// A token for one of the solution's end users, for the embed widget or the apps API. Call it
    /// from your backend (with an API key) after your own sign-in: the user id is yours, and
    /// everything the token reaches is that user's alone.
    /// </summary>
    [HttpPost("{id}/end-user-tokens")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> EndUserToken(string id, [FromBody] SolutionAppService.TokenBody body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            if (row.Status != nameof(SolutionInstallStatus.Active)) return Conflict(new { error = "The solution isn't active." });
            return Ok(apps.IssueToken(row.TenantId, row.InstallId, body));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    // ---------------------------------------------------------------- app keys

    public sealed record AppKeyBody(string? Name);

    /// <summary>The solution's app keys (never the keys themselves): what a builder's backend uses on
    /// the apps API instead of an organization API key.</summary>
    [HttpGet("{id}/app-keys")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> AppKeys(string id, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            return Ok(await appKeys.ListAsync(row.TenantId, row.InstallId, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>A new app key for the solution. The key is in this response only.</summary>
    [HttpPost("{id}/app-keys")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> CreateAppKey(string id, [FromBody] AppKeyBody? body, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            if (row.Status != nameof(SolutionInstallStatus.Active)) return Conflict(new { error = "The solution isn't active." });
            var (key, secret) = await appKeys.CreateAsync(row.TenantId, row.InstallId, body?.Name, HttpContext.Caller().ActorId, ct);
            return StatusCode(201, new { key = secret, key_id = key.KeyId, name = key.Name, prefix = key.Prefix, install_id = row.InstallId, created_at = key.CreatedAt });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Revokes an app key: requests with it are refused from now on.</summary>
    [HttpDelete("{id}/app-keys/{keyId}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> RevokeAppKey(string id, string keyId, CancellationToken ct)
    {
        try
        {
            var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
            return await appKeys.RevokeAsync(row.TenantId, row.InstallId, keyId, HttpContext.Caller().ActorId, ct) ? NoContent() : NotFound(new { error = $"No app key '{keyId}'." });
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    // ---------------------------------------------------------------- records (the builder's backend)

    /// <summary>The solution, by id or name, in the caller's organization (uninstalled ones too, for reading).</summary>
    private async Task<InstalledSolution> SolutionAsync(string id, CancellationToken ct)
    {
        var row = await solutions.FindAsync(HttpContext.Caller().TenantId, id, ct);
        return await catalog.GetAsync(row.TenantId, row.InstallId, ct) ?? throw new SolutionException($"No solution '{id}'.", StatusCodes.Status404NotFound);
    }

    private string Writer => "api:" + HttpContext.Caller().ActorId;

    private async Task<IActionResult> Records(string id, Func<InstalledSolution, Task<IActionResult>> action, CancellationToken ct)
    {
        try
        {
            return await action(await SolutionAsync(id, ct));
        }
        catch (SolutionException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Lists records; see <see cref="SolutionRecordsApi.ListAsync"/> for the query.</summary>
    [HttpGet("{id}/records/{entity}")]
    public Task<IActionResult> ListRecords(string id, string entity, CancellationToken ct) =>
        Records(id, s => recordsApi.ListAsync(s, entity, Request.Query, ct), ct);

    [HttpGet("{id}/records/{entity}/{recordId}")]
    public Task<IActionResult> GetRecord(string id, string entity, string recordId, CancellationToken ct) =>
        Records(id, s => recordsApi.GetAsync(s, entity, recordId, ct), ct);

    /// <summary>Creates a record (the body is its fields). An Idempotency-Key header makes a retried
    /// create return the record the first one made.</summary>
    [HttpPost("{id}/records/{entity}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> CreateRecord(string id, string entity, [FromBody] JsonObject fields, CancellationToken ct) =>
        Records(id, s => recordsApi.CreateAsync(s, entity, fields, Request.Headers["Idempotency-Key"].ToString(), $"api:{HttpContext.Caller().TenantId}", Writer, ct), ct);

    /// <summary>Changes fields (null clears one). <c>If-Match: &lt;version&gt;</c> refuses the change
    /// if the record changed since that version.</summary>
    [HttpPatch("{id}/records/{entity}/{recordId}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> UpdateRecord(string id, string entity, string recordId, [FromBody] JsonObject fields, CancellationToken ct) =>
        Records(id, s => recordsApi.UpdateAsync(s, entity, recordId, fields, Request.Headers.IfMatch.ToString(), Writer, ct), ct);

    [HttpDelete("{id}/records/{entity}/{recordId}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> DeleteRecord(string id, string entity, string recordId, CancellationToken ct) =>
        Records(id, s => recordsApi.DeleteAsync(s, entity, recordId, Writer, ct), ct);
}
