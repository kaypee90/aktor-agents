using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Resolves a tool-supplied relative path to an absolute path inside the per-task workspace,
/// refusing anything that would escape the sandbox (CLAUDE.md section 19/44).
/// </summary>
public static class WorkspacePath
{
    public static string Resolve(ToolsOptions options, string taskId, string relativePath)
    {
        var taskRoot = Path.GetFullPath(Path.Combine(options.WorkspaceRoot, SafeSegment(taskId)));
        Directory.CreateDirectory(taskRoot);

        var combined = Path.GetFullPath(Path.Combine(taskRoot, relativePath.TrimStart('/', '\\')));
        // Compare against the root plus a separator: a bare prefix check lets "../<taskId>x/..."
        // resolve to a sibling directory that merely shares the task root's name as a prefix.
        var rootWithSeparator = Path.EndsInDirectorySeparator(taskRoot) ? taskRoot : taskRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, taskRoot, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Path escapes the task workspace sandbox.");
        }

        return combined;
    }

    public static string TaskRoot(ToolsOptions options, string taskId)
    {
        var root = Path.GetFullPath(Path.Combine(options.WorkspaceRoot, SafeSegment(taskId)));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>True if <paramref name="fullPath"/> is inside the task's workspace root.</summary>
    public static bool IsInsideTaskRoot(ToolsOptions options, string taskId, string fullPath)
    {
        var taskRoot = Path.GetFullPath(Path.Combine(options.WorkspaceRoot, SafeSegment(taskId)));
        var rootWithSeparator = Path.EndsInDirectorySeparator(taskRoot) ? taskRoot : taskRoot + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string SafeSegment(string value) =>
        string.IsNullOrWhiteSpace(value) ? "shared" : string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}

public sealed class FilesystemReadTool(IOptions<ToolsOptions> options) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "filesystem_read",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Read a text file from your task's sandboxed workspace.",
        RequiredPermissions = ToolPermission.ReadFilesystem,
        JsonSchema = """{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<PathArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid filesystem_read arguments.");
        var fullPath = WorkspacePath.Resolve(options.Value, request.TaskId, args.Path);

        if (!File.Exists(fullPath))
        {
            return ToolExecutionResult.Fail($"File '{args.Path}' does not exist.");
        }

        var content = await File.ReadAllTextAsync(fullPath, request.CancellationToken);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { path = args.Path, content }, ToolJson.Options));
    }

    private sealed record PathArgs(string Path);
}

public sealed class FilesystemWriteTool(IOptions<ToolsOptions> options, IEventPublisher events) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "filesystem_write",
        SideEffects = ToolSideEffects.Idempotent,
        Description = "Write a text file (e.g. a report or code artifact) into your task's sandboxed workspace.",
        RequiredPermissions = ToolPermission.WriteFilesystem,
        JsonSchema = """
        {
          "type": "object",
          "properties": { "path": { "type": "string" }, "content": { "type": "string" } },
          "required": ["path", "content"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<WriteArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid filesystem_write arguments.");
        var fullPath = WorkspacePath.Resolve(options.Value, request.TaskId, args.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, args.Content, request.CancellationToken);

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.ArtifactCreated,
            AgentId = request.AgentId,
            TaskId = request.TaskId,
            Summary = $"Agent '{request.AgentId}' created artifact '{args.Path}'.",
            Data = new Dictionary<string, string>
            {
                ["artifactId"] = Guid.NewGuid().ToString("n"),
                ["type"] = ArtifactType.Document.ToString(),
                ["location"] = fullPath
            }
        }, request.CancellationToken);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { path = args.Path, bytesWritten = args.Content.Length }, ToolJson.Options));
    }

    private sealed record WriteArgs(string Path, string Content);
}

public sealed class FilesystemListTool(IOptions<ToolsOptions> options) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "filesystem_list",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "List files/directories under a path in your task's sandboxed workspace.",
        RequiredPermissions = ToolPermission.ReadFilesystem,
        JsonSchema = """{ "type": "object", "properties": { "path": { "type": "string", "default": "." } } }"""
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<PathArgs>(request.ArgumentsJson, ToolJson.Options) ?? new PathArgs(".");
        var fullPath = WorkspacePath.Resolve(options.Value, request.TaskId, args.Path);

        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new { entries = Array.Empty<string>() }, ToolJson.Options)));
        }

        var entries = Directory.EnumerateFileSystemEntries(fullPath).Select(Path.GetFileName).ToList();
        return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new { entries }, ToolJson.Options)));
    }

    private sealed record PathArgs(string Path);
}
