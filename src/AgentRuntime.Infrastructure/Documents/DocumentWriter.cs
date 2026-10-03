using System.Text;

namespace AgentRuntime.Infrastructure.Documents;

public sealed record SheetSpec(string? Name, List<List<string>> Rows);

public sealed record SlideSpec(string? Title, List<string>? Bullets);

/// <summary>
/// What to put in a generated document. <see cref="Content"/> is Markdown (Word, PDF, Markdown);
/// <see cref="Sheets"/> are tables (Excel, CSV); <see cref="Slides"/> are title-and-bullet slides
/// (PowerPoint). When the matching part is missing, it is derived from <see cref="Content"/>:
/// Markdown tables become sheets, and "##" sections become slides.
/// </summary>
public sealed record DocumentSpec
{
    public string? Title { get; init; }
    public string? Content { get; init; }
    public List<SheetSpec>? Sheets { get; init; }
    public List<SlideSpec>? Slides { get; init; }
}

/// <summary>Writes Markdown, CSV, Word (.docx), PDF, Excel (.xlsx) and PowerPoint (.pptx) files.</summary>
public static class DocumentWriter
{
    /// <summary>The format for a path's extension, or null if create_document can't write it.</summary>
    public static string? FormatOf(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "markdown" => "md",
            "ppt" => "pptx",
            "doc" => "docx",
            "xls" => "xlsx",
            _ => DocumentFormats.WritableFormats.Contains(ext) ? ext : null
        };
    }

    public static byte[] Write(string format, DocumentSpec spec) => format switch
    {
        "md" => Encoding.UTF8.GetBytes(MarkdownOf(spec)),
        "csv" => Encoding.UTF8.GetBytes(Csv(SheetsOf(spec).FirstOrDefault()?.Rows ?? [])),
        "docx" => WordWriter.Write(spec),
        "pdf" => PdfWriter.Write(spec),
        "xlsx" => ExcelWriter.Write(SheetsOf(spec)),
        "pptx" => PowerPointWriter.Write(spec.Title, SlidesOf(spec)),
        _ => throw new ArgumentException($"Unsupported format '{format}'. Use one of: {string.Join(", ", DocumentFormats.WritableFormats)}.")
    };

    private static string MarkdownOf(DocumentSpec spec)
    {
        var body = spec.Content ?? string.Empty;
        if (spec.Sheets is { Count: > 0 } sheets && string.IsNullOrWhiteSpace(body))
        {
            body = string.Join("\n", sheets.Select(s => $"## {s.Name}\n\n{DocumentReader.MarkdownTable(s.Rows)}"));
        }

        return string.IsNullOrWhiteSpace(spec.Title) || body.TrimStart().StartsWith("# ") ? body : $"# {spec.Title}\n\n{body}";
    }

    /// <summary>The sheets given, or the Markdown tables in the content, or the content's lines.</summary>
    public static List<SheetSpec> SheetsOf(DocumentSpec spec)
    {
        if (spec.Sheets is { Count: > 0 } sheets) return sheets;
        var blocks = MarkdownBlocks.Parse(spec.Content ?? string.Empty);
        var tables = blocks.OfType<TableBlock>().ToList();
        if (tables.Count > 0)
        {
            return tables.Select((t, i) => new SheetSpec(tables.Count == 1 ? spec.Title : $"Table {i + 1}",
                t.Rows.Select(r => r.Select(MarkdownBlocks.PlainText).ToList()).ToList())).ToList();
        }

        var lines = (spec.Content ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
        return [new SheetSpec(spec.Title, lines.Select(l => new List<string> { l }).ToList())];
    }

    /// <summary>The slides given, or one slide per "#"/"##" section of the content.</summary>
    public static List<SlideSpec> SlidesOf(DocumentSpec spec)
    {
        if (spec.Slides is { Count: > 0 } slides) return slides;
        var result = new List<SlideSpec>();
        string? title = null;
        var bullets = new List<string>();
        foreach (var block in MarkdownBlocks.Parse(spec.Content ?? string.Empty))
        {
            if (block is HeadingBlock { Level: <= 2 } h)
            {
                if (title is not null || bullets.Count > 0) result.Add(new SlideSpec(title, bullets));
                title = MarkdownBlocks.PlainText(h.Spans);
                bullets = [];
                continue;
            }

            var text = block switch
            {
                HeadingBlock h3 => MarkdownBlocks.PlainText(h3.Spans),
                ParagraphBlock p => MarkdownBlocks.PlainText(p.Spans),
                ListItemBlock li => MarkdownBlocks.PlainText(li.Spans),
                QuoteBlock q => MarkdownBlocks.PlainText(q.Spans),
                CodeBlock c => string.Join(" ", c.Lines),
                TableBlock t => string.Join("; ", t.Rows.Select(r => string.Join(" | ", r.Select(MarkdownBlocks.PlainText)))),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) bullets.Add(text);
        }

        if (title is not null || bullets.Count > 0) result.Add(new SlideSpec(title, bullets));
        return result.Count > 0 ? result : [new SlideSpec(spec.Title, [])];
    }

    /// <summary>RFC 4180: fields with commas, quotes or newlines are quoted, quotes doubled.</summary>
    public static string Csv(IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(f =>
                f.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{f.Replace("\"", "\"\"")}\"" : f)));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }
}
