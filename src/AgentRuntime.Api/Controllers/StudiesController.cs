using AgentRuntime.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Studies (docs/studies.md): research projects with their own datasets, documents and connections,
/// run by agent teams that analyze, model, simulate and report with cited evidence. Documents use
/// /api/memory?workspace={workspace_id} and connections /api/workspaces/{workspace_id}/connections.
/// </summary>
[ApiController]
[Route("api/studies")]
public sealed class StudiesController(StudyService studies, TenantAccess access) : ControllerBase
{
    /// <summary>model: an organization model profile id ("server" for the server's); left out, the organization's default.</summary>
    public sealed record CreateBody(string Name, string? Question, string? Model);
    /// <summary>model: a profile id, or "" for the organization's default; left out, unchanged.</summary>
    public sealed record UpdateBody(string? Name, string? Question, string? Model);
    public sealed record DatasetBody(Dictionary<string, string>? Dictionary, string? TimeColumn, double? HoldoutFraction);
    public sealed record RunBody(string? Instructions, string? Model);

    private async Task<IActionResult> Handle(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StudyServiceException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
        catch (TaskServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    private string Who() => HttpContext.Caller() is var c && c.Email is { } email ? email : c.ActorId;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await studies.ListAsync(access.TenantId, ct));

    [HttpPost]
    [Authorize(Policies.Member)]
    public Task<IActionResult> Create([FromBody] CreateBody body, CancellationToken ct) => Handle(async () =>
    {
        var study = await studies.CreateAsync(access.TenantId, body.Name ?? string.Empty, body.Question, Who(), ct, body.Model);
        return Ok(await studies.GetDetailAsync(access.TenantId, study.StudyId, ct));
    });

    [HttpGet("{id}")]
    public Task<IActionResult> Get(string id, CancellationToken ct) => Handle(async () => Ok(await studies.GetDetailAsync(access.TenantId, id, ct)));

    [HttpPatch("{id}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> Update(string id, [FromBody] UpdateBody body, CancellationToken ct) => Handle(async () =>
    {
        await studies.UpdateAsync(access.TenantId, id, body.Name, body.Question, ct, body.Model);
        return Ok(await studies.GetDetailAsync(access.TenantId, id, ct));
    });

    /// <summary>Deletes the study and everything in it (datasets, documents, connections, evidence, reports).</summary>
    [HttpDelete("{id}")]
    [Authorize(Policies.Admin)]
    public Task<IActionResult> Delete(string id, CancellationToken ct) => Handle(async () =>
    {
        await studies.DeleteAsync(access.TenantId, id, ct);
        return NoContent();
    });

    /// <summary>Adds datasets (multipart "files"); a file named like an existing dataset adds a new version.</summary>
    [HttpPost("{id}/datasets")]
    [Authorize(Policies.Member)]
    [RequestSizeLimit(300L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300L * 1024 * 1024)]
    public Task<IActionResult> AddDatasets(string id, [FromForm] List<IFormFile> files, CancellationToken ct) => Handle(async () =>
        Ok(await studies.AddDatasetsAsync(access.TenantId, id,
            files.Select(f => new StudyService.DatasetUpload(f.FileName, f.Length, f.OpenReadStream)).ToList(), ct)));

    [HttpPut("{id}/datasets/{datasetId}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> UpdateDataset(string id, string datasetId, [FromBody] DatasetBody body, CancellationToken ct) => Handle(async () =>
        Ok(await studies.UpdateDatasetAsync(access.TenantId, id, datasetId, body.Dictionary, body.TimeColumn, body.HoldoutFraction, ct)));

    [HttpDelete("{id}/datasets/{datasetId}")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> DeleteDataset(string id, string datasetId, CancellationToken ct) => Handle(async () =>
    {
        await studies.DeleteDatasetAsync(access.TenantId, id, datasetId, ct);
        return NoContent();
    });

    /// <summary>The training rows as Parquet (for the notebook export); the holdout stays sealed.</summary>
    [HttpGet("{id}/datasets/{datasetId}/download")]
    public Task<IActionResult> DownloadDataset(string id, string datasetId, CancellationToken ct) => Handle(async () =>
    {
        var (path, name) = await studies.DatasetFileAsync(access.TenantId, id, datasetId, ct);
        return PhysicalFile(path, "application/vnd.apache.parquet", name);
    });

    /// <summary>Starts an agent team on the study's question.</summary>
    [HttpPost("{id}/runs")]
    [Authorize(Policies.Member)]
    public Task<IActionResult> Run(string id, [FromBody] RunBody? body, CancellationToken ct) => Handle(async () =>
        Ok(await studies.StartRunAsync(access.TenantId, id, body?.Instructions, HttpContext.Caller().ActorId, body?.Model, ct)));

    [HttpGet("{id}/evidence")]
    public Task<IActionResult> Evidence(string id, CancellationToken ct) => Handle(async () => Ok(await studies.ListEvidenceAsync(access.TenantId, id, ct)));

    [HttpGet("{id}/evidence/{evidenceId}")]
    public Task<IActionResult> EvidenceItem(string id, string evidenceId, CancellationToken ct) => Handle(async () =>
        Ok(await studies.GetEvidenceAsync(access.TenantId, id, evidenceId, ct)));

    [HttpGet("{id}/files/{name}")]
    public Task<IActionResult> Chart(string id, string name, CancellationToken ct) => Handle(async () =>
        PhysicalFile(await studies.ChartPathAsync(access.TenantId, id, name, ct), "image/png"));

    [HttpGet("{id}/notebook")]
    public Task<IActionResult> Notebook(string id, CancellationToken ct) => Handle(async () =>
        File(System.Text.Encoding.UTF8.GetBytes(await studies.NotebookAsync(access.TenantId, id, ct)), "application/x-ipynb+json", $"{id}.ipynb"));
}
