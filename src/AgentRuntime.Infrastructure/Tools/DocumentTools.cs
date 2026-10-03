using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure.Documents;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Writes a finished document into the task workspace in the format people use: Word, PDF, Excel,
/// PowerPoint, CSV or Markdown. The format comes from the path's extension (or "format"). Like
/// filesystem_write it is confined to the task's sandbox and records the file as an artifact.
/// </summary>
public sealed class CreateDocumentTool(IOptions<ToolsOptions> options, IEventPublisher events) : ITool
{
    /// <summary>Generated files above this size are refused (a runaway table, say).</summary>
    private const int MaxOutputBytes = 25 * 1024 * 1024;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "create_document",
        SideEffects = ToolSideEffects.Idempotent,
        Description =
            "Create a document file in your task workspace: Word (.docx), PDF (.pdf), Excel (.xlsx), PowerPoint (.pptx), CSV (.csv) " +
            "or Markdown (.md); the path's extension picks the format. Word, PDF and Markdown take 'content' as Markdown (headings, " +
            "lists, tables, **bold**). Excel and CSV take 'sheets' (each a name and rows of cells, first row the header). PowerPoint " +
            "takes 'slides' (each a title and bullets). Without sheets or slides they are derived from 'content' (its tables become " +
            "sheets; its ## sections become slides). Overwrites a file at the same path. List the file in complete_task's artifacts.",
        RequiredPermissions = ToolPermission.WriteFilesystem,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "e.g. reports/market-analysis.docx" },
            "format": { "type": "string", "enum": ["docx", "pdf", "xlsx", "pptx", "csv", "md"], "description": "Overrides the extension." },
            "title": { "type": "string" },
            "content": { "type": "string", "description": "Markdown body (docx, pdf, md)." },
            "sheets": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } }
                },
                "required": ["rows"]
              }
            },
            "slides": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "bullets": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          },
          "required": ["path"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        using var doc = JsonDocument.Parse(request.ArgumentsJson);
        var args = doc.RootElement;
        var path = Str(args, "path");
        if (string.IsNullOrWhiteSpace(path)) return ToolExecutionResult.Fail("path is required.");

        var format = Str(args, "format")?.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            null or "" => DocumentWriter.FormatOf(path),
            "word" => "docx",
            "excel" => "xlsx",
            "powerpoint" or "ppt" => "pptx",
            "markdown" => "md",
            var f => DocumentFormats.WritableFormats.Contains(f) ? f : null
        };
        if (format is null)
        {
            return ToolExecutionResult.Fail(
                $"Can't tell which format to write for '{path}'. Use one of these extensions: .{string.Join(", .", DocumentFormats.WritableFormats)}.");
        }

        // The extension always matches the format, so the file opens in the right application.
        if (!path.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))
        {
            path = Path.ChangeExtension(path, format);
        }

        var spec = new DocumentSpec
        {
            Title = Str(args, "title"),
            Content = Str(args, "content"),
            Sheets = args.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array
                ? sheets.EnumerateArray().Select(s => new SheetSpec(Str(s, "name"), Rows(s))).ToList()
                : null,
            Slides = args.TryGetProperty("slides", out var slides) && slides.ValueKind == JsonValueKind.Array
                ? slides.EnumerateArray().Select(s => new SlideSpec(Str(s, "title"),
                    s.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array ? b.EnumerateArray().Select(Scalar).ToList() : [])).ToList()
                : null
        };

        if (string.IsNullOrWhiteSpace(spec.Content) && spec.Sheets is not { Count: > 0 } && spec.Slides is not { Count: > 0 })
        {
            return ToolExecutionResult.Fail("Give the document something to contain: content (Markdown), sheets or slides.");
        }

        var fullPath = WorkspacePath.Resolve(options.Value, request.TaskId, path);
        byte[] bytes;
        try
        {
            bytes = DocumentWriter.Write(format, spec);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return ToolExecutionResult.Fail($"Couldn't build the {format} file: {ex.Message}");
        }

        if (bytes.Length > MaxOutputBytes) return ToolExecutionResult.Fail("The document is too large (25 MB at most); split it into several files.");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, bytes, request.CancellationToken);

        await events.PublishAsync(new RuntimeEvent
        {
            Type = RuntimeEventType.ArtifactCreated,
            AgentId = request.AgentId,
            TaskId = request.TaskId,
            TenantId = request.TenantId,
            Summary = $"Agent '{request.AgentId}' created {format.ToUpperInvariant()} document '{path}'.",
            Data = new Dictionary<string, string>
            {
                ["artifactId"] = Guid.NewGuid().ToString("n"),
                ["type"] = (format is "xlsx" or "csv" ? ArtifactType.Data : ArtifactType.Document).ToString(),
                ["location"] = fullPath,
                ["format"] = format
            }
        }, request.CancellationToken);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            path = path.Replace('\\', '/'),
            format,
            size = DocumentFormats.HumanSize(bytes.Length)
        }, ToolJson.Options));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? Scalar(v) : null;

    /// <summary>Cells may come as numbers or booleans; they're written as text and re-typed by the writer.</summary>
    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        _ => v.GetRawText()
    };

    private static List<List<string>> Rows(JsonElement sheet) =>
        sheet.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(Scalar).ToList() : [Scalar(r)]).ToList()
            : [];
}
