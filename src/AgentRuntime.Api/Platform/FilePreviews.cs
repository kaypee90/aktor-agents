using AgentRuntime.Infrastructure.Documents;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// What the dashboard needs to show a file in place: <c>kind</c> picks the viewer — markdown
/// (Markdown and Word), code, text, table (CSV and Excel, one entry per sheet), slides
/// (PowerPoint), pdf and image (shown from the file itself), html (source, shown sandboxed), or
/// binary (download only). Text is capped, so a huge file previews its beginning.
/// </summary>
public static class FilePreviews
{
    /// <summary>Files above this size aren't opened for a preview; they can still be downloaded.</summary>
    private const long MaxPreviewBytes = 50L * 1024 * 1024;

    public static async Task<object> BuildAsync(string fullPath, string relativePath, string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        var size = new FileInfo(fullPath).Length;
        var kind = DocumentFormats.KindOf(fullPath);
        var language = DocumentFormats.CodeLanguageOf(fullPath);
        DocumentContent? content = null;
        if (size <= MaxPreviewBytes && kind is not (DocumentKind.Image or DocumentKind.Binary))
        {
            content = await DocumentReader.ReadAsync(fullPath, new ReadLimits(MaxChars: 400_000, MaxRowsPerSheet: 2_000), ct);
        }

        var view = kind switch
        {
            _ when size > MaxPreviewBytes => "binary",
            DocumentKind.Markdown or DocumentKind.Word => "markdown",
            DocumentKind.Text => language is null ? "text" : "code",
            DocumentKind.Csv or DocumentKind.Excel => "table",
            DocumentKind.PowerPoint => "slides",
            DocumentKind.Pdf => "pdf",
            DocumentKind.Image => "image",
            DocumentKind.Html => "html",
            _ => "binary"
        };

        return new
        {
            file_name = Path.GetFileName(fullPath),
            path = relativePath,
            kind = view,
            format = kind.ToString().ToLowerInvariant(),
            content_type = DocumentFormats.ContentTypeOf(fullPath),
            size_bytes = size,
            created_by = createdBy,
            created_at = createdAt,
            language,
            text = view is "table" or "slides" ? null : content?.Text,
            sheets = content?.Sheets?.Select(s => new { name = s.Name, rows = s.Rows, truncated = s.Truncated }),
            slides = content?.Slides?.Select(s => new { number = s.Number, title = s.Title, paragraphs = s.Paragraphs, notes = s.Notes }),
            truncated = content?.Truncated ?? false,
            note = size > MaxPreviewBytes
                ? $"This file is too large to preview ({DocumentFormats.HumanSize(size)}). Download it to open it."
                : content?.Note
        };
    }
}
