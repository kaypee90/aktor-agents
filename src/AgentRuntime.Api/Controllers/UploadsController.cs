using AgentRuntime.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRuntime.Api.Controllers;

/// <summary>
/// Files attached while writing a new task, before the task exists (multipart field "files").
/// Pass the returned upload ids as the new task's "attachments"; unclaimed uploads expire in a day.
/// </summary>
[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(UploadStore uploads, TenantAccess access) : ControllerBase
{
    [HttpPost]
    [Authorize(Policies.Member)]
    [RequestSizeLimit(300L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 300L * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] List<IFormFile> files, CancellationToken ct)
    {
        try
        {
            var saved = await uploads.SaveAsync(access.TenantId,
                files.Select(f => new AttachmentUpload(f.FileName, f.Length, f.OpenReadStream)).ToList(), ct);
            return Ok(saved.Select(s => new { upload_id = s.UploadId, file_name = s.FileName, size_bytes = s.SizeBytes }));
        }
        catch (TaskServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }
}
