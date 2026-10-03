using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AgentRuntime.Infrastructure.Documents;

public sealed record SheetData(string Name, List<List<string>> Rows, bool Truncated);

public sealed record SlideData(int Number, string? Title, List<string> Paragraphs, string? Notes);

/// <summary>
/// A file's content as text an agent can read (Markdown for documents, tables for spreadsheets),
/// plus the structure a preview shows: sheets for spreadsheets and CSV, slides for decks.
/// </summary>
public sealed record DocumentContent
{
    public required DocumentKind Kind { get; init; }
    public required string Text { get; init; }
    public bool Truncated { get; init; }
    public List<SheetData>? Sheets { get; init; }
    public List<SlideData>? Slides { get; init; }
    /// <summary>Why there is little or no text (an image, a scanned PDF, an unsupported format).</summary>
    public string? Note { get; init; }
}

/// <summary>Limits on what is read: text length, and rows per sheet.</summary>
public sealed record ReadLimits(int MaxChars = 100_000, int MaxRowsPerSheet = 500);

/// <summary>
/// Reads text out of the files people attach and agents produce: plain text and code, Markdown,
/// CSV/TSV, HTML (as source), PDF, Word (.docx), Excel (.xlsx) and PowerPoint (.pptx). Images and
/// other binary files get a short description instead. Never throws for a damaged file: it reports
/// that it couldn't be read.
/// </summary>
public static class DocumentReader
{
    public static async Task<DocumentContent> ReadAsync(string path, ReadLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new ReadLimits();
        var kind = DocumentFormats.KindOf(path);
        var size = new FileInfo(path).Length;
        try
        {
            return kind switch
            {
                DocumentKind.Text or DocumentKind.Markdown or DocumentKind.Html => Clip(kind, await File.ReadAllTextAsync(path, ct), limits),
                DocumentKind.Csv => ReadCsv(path, await File.ReadAllTextAsync(path, ct), limits),
                DocumentKind.Pdf => ReadPdf(path, limits),
                DocumentKind.Word => ReadWord(path, limits),
                DocumentKind.Excel => ReadExcel(path, limits),
                DocumentKind.PowerPoint => ReadPowerPoint(path, limits),
                DocumentKind.Image => new DocumentContent
                {
                    Kind = kind,
                    Text = string.Empty,
                    Note = $"Image file ({DocumentFormats.HumanSize(size)}). Its pixels can't be read as text."
                },
                _ => new DocumentContent
                {
                    Kind = kind,
                    Text = string.Empty,
                    Note = Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".xls" or ".ppt"
                        ? "Legacy Office format: its text can't be read. Save it as .docx, .xlsx or .pptx to share its content."
                        : $"Binary file ({DocumentFormats.HumanSize(size)}); its content can't be read as text."
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DocumentContent { Kind = kind, Text = string.Empty, Note = $"The file couldn't be read ({ex.GetType().Name}): it may be damaged or password-protected." };
        }
    }

    private static DocumentContent Clip(DocumentKind kind, string text, ReadLimits limits, List<SheetData>? sheets = null,
        List<SlideData>? slides = null, string? note = null, bool truncated = false) =>
        new()
        {
            Kind = kind,
            Text = text.Length > limits.MaxChars ? text[..limits.MaxChars] : text,
            Truncated = truncated || text.Length > limits.MaxChars,
            Sheets = sheets,
            Slides = slides,
            Note = note
        };

    // ---- CSV ----------------------------------------------------------------------------------

    private static DocumentContent ReadCsv(string path, string text, ReadLimits limits)
    {
        var delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(text);
        var rows = ParseDelimited(text, delimiter, limits.MaxRowsPerSheet + 1);
        var truncated = rows.Count > limits.MaxRowsPerSheet;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        var sheet = new SheetData(Path.GetFileNameWithoutExtension(path), rows, truncated);
        // Agents get the file as written; the table is for the preview.
        return Clip(DocumentKind.Csv, text, limits, sheets: [sheet]);
    }

    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        return new[] { ',', ';', '\t', '|' }.MaxBy(c => firstLine.Count(x => x == c));
    }

    /// <summary>RFC 4180 parsing: quoted fields may hold the delimiter, quotes ("") and newlines.</summary>
    public static List<List<string>> ParseDelimited(string text, char delimiter, int maxRows = int.MaxValue)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length && rows.Count < maxRows; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }

        if ((field.Length > 0 || row.Count > 0) && rows.Count < maxRows)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    // ---- PDF ----------------------------------------------------------------------------------

    private static DocumentContent ReadPdf(string path, ReadLimits limits)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            if (sb.Length > limits.MaxChars) break;
            var text = ContentOrderTextExtractor.GetText(page);
            if (pdf.NumberOfPages > 1) sb.Append("## Page ").Append(page.Number).Append("\n\n");
            sb.Append(text.Trim()).Append("\n\n");
        }

        var note = sb.ToString().Count(char.IsLetterOrDigit) < 20 * pdf.NumberOfPages
            ? "Little or no text was found: this PDF may be scanned images, which can't be read as text."
            : null;
        return Clip(DocumentKind.Pdf, sb.ToString().Trim(), limits, note: note);
    }

    // ---- Word ---------------------------------------------------------------------------------

    private static DocumentContent ReadWord(string path, ReadLimits limits)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Clip(DocumentKind.Word, string.Empty, limits, note: "The document is empty.");

        var styles = doc.MainDocumentPart!.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
            .Where(s => s.StyleId?.Value is not null)
            .ToDictionary(s => s.StyleId!.Value!, s => s.StyleName?.Val?.Value ?? s.StyleId!.Value!) ?? [];
        var sb = new StringBuilder();

        foreach (var element in body.ChildElements)
        {
            if (sb.Length > limits.MaxChars) break;
            switch (element)
            {
                case W.Paragraph p:
                    var text = ParagraphText(p);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var styleName = styleId is null ? string.Empty : styles.GetValueOrDefault(styleId, styleId);
                    var level = HeadingLevel(styleName);
                    if (level > 0) sb.Append(new string('#', level)).Append(' ').Append(text).Append("\n\n");
                    else if (p.ParagraphProperties?.NumberingProperties is not null || styleName.StartsWith("List", StringComparison.OrdinalIgnoreCase))
                    {
                        var indent = p.ParagraphProperties?.NumberingProperties?.NumberingLevelReference?.Val?.Value ?? 0;
                        sb.Append(new string(' ', indent * 2)).Append("- ").Append(text).Append('\n');
                    }
                    else sb.Append(text).Append("\n\n");
                    break;

                case W.Table table:
                    var rows = table.Elements<W.TableRow>()
                        .Select(r => r.Elements<W.TableCell>().Select(c => string.Join(" ", c.Elements<W.Paragraph>().Select(ParagraphText))).ToList())
                        .ToList();
                    sb.Append(MarkdownTable(rows)).Append('\n');
                    break;
            }
        }

        return Clip(DocumentKind.Word, sb.ToString().Trim(), limits);
    }

    private static string ParagraphText(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case W.Text t: sb.Append(t.Text); break;
                case W.TabChar: sb.Append('\t'); break;
                case W.Break: sb.Append('\n'); break;
            }
        }

        return sb.ToString().Trim();
    }

    private static int HeadingLevel(string styleName)
    {
        if (styleName.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        var name = styleName.Replace(" ", string.Empty);
        return name.StartsWith("heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[7..], out var n) ? Math.Clamp(n, 1, 6) : 0;
    }

    // ---- Excel --------------------------------------------------------------------------------

    private static DocumentContent ReadExcel(string path, ReadLimits limits)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var workbook = doc.WorkbookPart!;
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        var sheets = new List<SheetData>();
        var sb = new StringBuilder();
        var truncatedAny = false;

        foreach (var sheet in workbook.Workbook.Sheets?.Elements<S.Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relId || workbook.GetPartById(relId) is not WorksheetPart part) continue;
            var rows = new List<List<string>>();
            var truncated = false;
            foreach (var row in part.Worksheet.Descendants<S.Row>())
            {
                if (rows.Count >= limits.MaxRowsPerSheet) { truncated = true; break; }
                var cells = new List<string>();
                foreach (var cell in row.Elements<S.Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value) ?? cells.Count;
                    while (cells.Count < column) cells.Add(string.Empty);
                    cells.Add(CellText(cell, shared));
                }

                // Keep blank rows inside the data, but not a sheet's trailing ones.
                rows.Add(cells);
            }

            while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
            var name = sheet.Name?.Value ?? $"Sheet{sheets.Count + 1}";
            sheets.Add(new SheetData(name, rows, truncated));
            truncatedAny |= truncated;
            sb.Append("## ").Append(name).Append("\n\n").Append(rows.Count == 0 ? "(empty)\n" : MarkdownTable(rows)).Append('\n');
            if (truncated) sb.Append($"(first {limits.MaxRowsPerSheet} rows only)\n\n");
        }

        return Clip(DocumentKind.Excel, sb.ToString().Trim(), limits, sheets: sheets, truncated: truncatedAny);
    }

    private static string CellText(S.Cell cell, List<string> shared)
    {
        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString) return int.TryParse(value, out var i) && i < shared.Count ? shared[i] : value;
        if (cell.DataType?.Value == S.CellValues.InlineString) return cell.InlineString?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.Boolean) return value == "1" ? "TRUE" : "FALSE";
        return value;
    }

    /// <summary>"C7" → 2 (zero-based column).</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var column = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter)) column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return column == 0 ? null : column - 1;
    }

    // ---- PowerPoint ---------------------------------------------------------------------------

    private static DocumentContent ReadPowerPoint(string path, ReadLimits limits)
    {
        using var doc = PresentationDocument.Open(path, false);
        var presentation = doc.PresentationPart!;
        var slides = new List<SlideData>();
        var sb = new StringBuilder();
        var number = 0;

        foreach (var slideId in presentation.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            number++;
            if (slideId.RelationshipId?.Value is not { } relId || presentation.GetPartById(relId) is not SlidePart part) continue;

            string? title = null;
            var paragraphs = new List<string>();
            foreach (var shape in part.Slide.Descendants<P.Shape>())
            {
                var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>();
                // A title placeholder, or a text box named as one (decks written without placeholders).
                var isTitle = placeholder?.Type?.Value == P.PlaceholderValues.Title || placeholder?.Type?.Value == P.PlaceholderValues.CenteredTitle ||
                              (placeholder is null && shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "Title");
                var texts = shape.TextBody?.Elements<A.Paragraph>().Select(p => p.InnerText.Trim()).Where(t => t.Length > 0).ToList() ?? [];
                if (isTitle && title is null) title = string.Join(" ", texts);
                else paragraphs.AddRange(texts);
            }

            // Tables on the slide.
            foreach (var table in part.Slide.Descendants<A.Table>())
            {
                var rows = table.Elements<A.TableRow>().Select(r => r.Elements<A.TableCell>().Select(c => c.InnerText.Trim()).ToList()).ToList();
                paragraphs.Add(MarkdownTable(rows).TrimEnd());
            }

            var notes = part.NotesSlidePart?.NotesSlide?.Descendants<P.Shape>()
                .Where(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>()?.Type?.Value == P.PlaceholderValues.Body)
                .Select(s => s.TextBody?.InnerText.Trim())
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));

            slides.Add(new SlideData(number, title, paragraphs, notes));
            sb.Append("## Slide ").Append(number).Append(title is null ? string.Empty : $": {title}").Append("\n\n");
            foreach (var p in paragraphs) sb.Append(p.StartsWith('|') ? p : $"- {p}").Append('\n');
            if (notes is not null) sb.Append("\nSpeaker notes: ").Append(notes).Append('\n');
            sb.Append('\n');
        }

        return Clip(DocumentKind.PowerPoint, sb.ToString().Trim(), limits, slides: slides);
    }

    // ---- Shared -------------------------------------------------------------------------------

    public static string MarkdownTable(List<List<string>> rows)
    {
        if (rows.Count == 0) return string.Empty;
        var width = rows.Max(r => r.Count);
        if (width == 0) return string.Empty;
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        void Row(IReadOnlyList<string> r) =>
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, width).Select(i => i < r.Count ? Cell(r[i]) : string.Empty))).Append(" |\n");

        Row(rows[0]);
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
        foreach (var r in rows.Skip(1)) Row(r);
        return sb.ToString();
    }
}
