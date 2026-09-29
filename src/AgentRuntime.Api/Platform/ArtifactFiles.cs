using System.IO.Compression;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// The files agents wrote with filesystem_write, for a task or a workspace (both keep files under
/// their own id in the sandbox root). Only files still inside that sandbox are ever served.
/// </summary>
public static class ArtifactFiles
{
    public sealed record FileView(ArtifactRecord Latest, string RelativePath, long SizeBytes, int Versions);

    /// <summary>One entry per file, newest write first. An agent rewriting a file records an
    /// artifact per write; the latest one describes the file as it is now.</summary>
    public static async Task<List<FileView>> ListAsync(AgentDbContext db, ToolsOptions opts, string scopeId, string tenantId, CancellationToken ct)
    {
        var records = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == scopeId && a.TenantId == tenantId)
            .ToListAsync(ct);

        return Latest(records, opts, scopeId);
    }

    /// <summary>Collapses artifact records to one per file still on disk inside the sandbox.</summary>
    public static List<FileView> Latest(IEnumerable<ArtifactRecord> records, ToolsOptions opts, string scopeId)
    {
        var root = WorkspacePath.TaskRoot(opts, scopeId);
        return records
            .Where(a => WorkspacePath.IsInsideTaskRoot(opts, scopeId, a.Location) && File.Exists(a.Location))
            .GroupBy(a => a.Location)
            .Select(g =>
            {
                var latest = g.MaxBy(a => a.CreatedAt)!;
                return new FileView(latest, Path.GetRelativePath(root, latest.Location).Replace(Path.DirectorySeparatorChar, '/'),
                    new FileInfo(latest.Location).Length, g.Count());
            })
            .OrderByDescending(f => f.Latest.CreatedAt)
            .ToList();
    }

    /// <summary>The files as one zip, keeping their folder layout relative to the sandbox root.</summary>
    public static async Task<MemoryStream> ZipAsync(IEnumerable<string> locations, string root, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in locations)
            {
                var entryName = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = await entry.OpenAsync(ct);
                await using var source = File.OpenRead(file);
                await source.CopyToAsync(entryStream, ct);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
