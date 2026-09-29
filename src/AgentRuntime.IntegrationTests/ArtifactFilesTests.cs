using System.IO.Compression;
using AgentRuntime.Api.Platform;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Tools;
using Xunit;

namespace AgentRuntime.IntegrationTests;

/// <summary>Which saved files a task or workspace offers for download, and the zip of them.</summary>
public sealed class ArtifactFilesTests : IDisposable
{
    private readonly ToolsOptions _opts = new() { WorkspaceRoot = Path.Combine(Path.GetTempPath(), $"aktor-files-{Guid.NewGuid():n}") };
    private const string Scope = "ws-files-test";

    public void Dispose()
    {
        if (Directory.Exists(_opts.WorkspaceRoot)) Directory.Delete(_opts.WorkspaceRoot, recursive: true);
    }

    private string Write(string scope, string relative, string content)
    {
        var path = WorkspacePath.Resolve(_opts, scope, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ArtifactRecord Record(string location, string agent, int minutesAgo) => new()
    {
        ArtifactId = Guid.NewGuid().ToString("n"),
        Type = "Document",
        Location = location,
        CreatedByAgent = agent,
        TaskId = Scope,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    [Fact]
    public void EachFileAppearsOnce_AsItsLatestWrite_NewestFirst()
    {
        var report = Write(Scope, "reports/nigeria.md", "final text");
        var notes = Write(Scope, "notes.txt", "notes");

        var files = ArtifactFiles.Latest(
        [
            Record(report, "agent-a", minutesAgo: 30),
            Record(notes, "agent-b", minutesAgo: 20),
            Record(report, "agent-c", minutesAgo: 5)
        ], _opts, Scope);

        Assert.Equal(["reports/nigeria.md", "notes.txt"], files.Select(f => f.RelativePath));
        var first = files[0];
        Assert.Equal("agent-c", first.Latest.CreatedByAgent);
        Assert.Equal(2, first.Versions);
        Assert.Equal("final text".Length, first.SizeBytes);
    }

    [Fact]
    public void FilesOutsideTheSandbox_OrDeleted_AreNeverOffered()
    {
        var other = Write("some-other-workspace", "secret.txt", "not yours");
        var outside = Path.Combine(Path.GetTempPath(), $"aktor-outside-{Guid.NewGuid():n}.txt");
        File.WriteAllText(outside, "host file");
        try
        {
            var deleted = Write(Scope, "gone.md", "x");
            File.Delete(deleted);

            var files = ArtifactFiles.Latest([Record(other, "a", 1), Record(outside, "a", 1), Record(deleted, "a", 1)], _opts, Scope);

            Assert.Empty(files);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TheZip_KeepsTheFolderLayout_AndCurrentContents()
    {
        var report = Write(Scope, "reports/togo.md", "togo report");
        var data = Write(Scope, "data.csv", "a,b");

        using var zip = new ZipArchive(await ArtifactFiles.ZipAsync([report, data], WorkspacePath.TaskRoot(_opts, Scope), default));

        Assert.Equal(["data.csv", "reports/togo.md"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("reports/togo.md")!.Open());
        Assert.Equal("togo report", await reader.ReadToEndAsync());
    }
}
