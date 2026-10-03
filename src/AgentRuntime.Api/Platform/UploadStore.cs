using AgentRuntime.Infrastructure.Documents;
using AgentRuntime.Infrastructure.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

/// <summary>A file uploaded before there is a task to put it in (the new-task composer).</summary>
public sealed record StagedUpload(string UploadId, string FileName, long SizeBytes, string FullPath);

/// <summary>
/// Holds files people attach while writing a new task, until the task starts and takes them into
/// its own workspace. Uploads live under the sandbox root in a folder per organization, so one
/// organization can never claim another's upload id; unclaimed uploads are deleted after a day.
/// </summary>
public sealed class UploadStore(IOptions<ToolsOptions> options, ILogger<UploadStore> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    private string TenantRoot(string tenantId) =>
        Path.Combine(Path.GetFullPath(options.Value.WorkspaceRoot), "_uploads",
            new string(tenantId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()) is { Length: > 0 } safe ? safe : "default");

    public async Task<IReadOnlyList<StagedUpload>> SaveAsync(string tenantId, IReadOnlyList<AttachmentUpload> uploads, CancellationToken ct)
    {
        var opts = options.Value;
        if (uploads.Count == 0) throw new TaskServiceException("Attach at least one file.");
        if (uploads.Count > opts.AttachmentMaxFiles) throw new TaskServiceException($"Attach at most {opts.AttachmentMaxFiles} files at a time.");
        if (uploads.FirstOrDefault(u => u.Length > opts.AttachmentMaxBytes) is { } big)
        {
            throw new TaskServiceException($"'{big.FileName}' is too large ({DocumentFormats.HumanSize(big.Length)}); files can be at most " +
                                           $"{DocumentFormats.HumanSize(opts.AttachmentMaxBytes)}.", StatusCodes.Status413PayloadTooLarge);
        }

        DeleteExpired(tenantId);
        var saved = new List<StagedUpload>();
        foreach (var upload in uploads)
        {
            var id = Guid.NewGuid().ToString("n");
            var dir = Path.Combine(TenantRoot(tenantId), id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, TaskService.SafeFileName(upload.FileName));
            await using (var target = File.Create(path))
            await using (var source = upload.Open())
            {
                await source.CopyToAsync(target, ct);
            }

            saved.Add(new StagedUpload(id, Path.GetFileName(path), upload.Length, path));
        }

        return saved;
    }

    /// <summary>The organization's uploads with these ids; throws if any is unknown or expired.</summary>
    public IReadOnlyList<StagedUpload> Find(string tenantId, IReadOnlyList<string> uploadIds)
    {
        var found = new List<StagedUpload>();
        foreach (var id in uploadIds.Distinct())
        {
            var dir = Path.Combine(TenantRoot(tenantId), new string(id.Where(char.IsLetterOrDigit).ToArray()));
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault() : null;
            if (file is null) throw new TaskServiceException("An attached file has expired or doesn't exist. Attach it again.");
            found.Add(new StagedUpload(id, Path.GetFileName(file), new FileInfo(file).Length, file));
        }

        return found;
    }

    /// <summary>Removes an upload once a task has taken it.</summary>
    public void Release(StagedUpload upload)
    {
        try
        {
            var dir = Path.GetDirectoryName(upload.FullPath)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't remove staged upload {UploadId}", upload.UploadId);
        }
    }

    private void DeleteExpired(string tenantId)
    {
        var root = TenantRoot(tenantId);
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > Lifetime) Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // In use or already gone; the next upload tries again.
            }
        }
    }
}
