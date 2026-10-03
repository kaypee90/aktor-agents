using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>Tables to an Excel workbook: one sheet each, a bold header row, numbers stored as
/// numbers, columns sized to their content, and the header frozen.</summary>
internal static class ExcelWriter
{
    private const uint HeaderStyle = 1;

    public static byte[] Write(List<SheetSpec> sheets)
    {
        if (sheets.Count == 0) sheets = [new SheetSpec("Sheet1", [])];
        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = doc.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            AddStyles(workbook);
            var sheetList = workbook.Workbook.AppendChild(new Sheets());
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < sheets.Count; i++)
            {
                var spec = sheets[i];
                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
                var columns = spec.Rows.Count == 0 ? 0 : spec.Rows.Max(r => r.Count);

                for (var r = 0; r < spec.Rows.Count; r++)
                {
                    var row = new Row { RowIndex = (uint)(r + 1) };
                    for (var c = 0; c < spec.Rows[r].Count; c++)
                    {
                        var cell = Cell(spec.Rows[r][c], $"{ColumnName(c)}{r + 1}");
                        if (r == 0) cell.StyleIndex = HeaderStyle;
                        row.Append(cell);
                    }

                    data.Append(row);
                }

                // Schema order: sheetViews, sheetFormatPr, cols, sheetData.
                var worksheet = new Worksheet();
                if (spec.Rows.Count > 1)
                {
                    worksheet.Append(new SheetViews(new SheetView(new Pane
                    {
                        VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen
                    }) { WorkbookViewId = 0 }));
                }

                if (columns > 0)
                {
                    worksheet.Append(new Columns(Enumerable.Range(0, columns).Select(c => new Column
                    {
                        Min = (uint)(c + 1), Max = (uint)(c + 1), CustomWidth = true,
                        Width = Math.Clamp(spec.Rows.Max(r => c < r.Count ? r[c].Length : 0) + 2, 8, 60)
                    })));
                }

                worksheet.Append(data);
                part.Worksheet = worksheet;
                part.Worksheet.Save();

                sheetList.Append(new Sheet
                {
                    Id = workbook.GetIdOfPart(part),
                    SheetId = (uint)(i + 1),
                    Name = UniqueName(spec.Name, i, names)
                });
            }

            workbook.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static Cell Cell(string value, string reference)
    {
        // Numbers as numbers (so sums and charts work), but keep identifiers like "007" as text.
        var trimmed = value.Trim();
        if (trimmed.Length is > 0 and < 16 && !(trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.') &&
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            return new Cell { CellReference = reference, CellValue = new CellValue(number), DataType = CellValues.Number };
        }

        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
        };
    }

    /// <summary>0 → A, 26 → AA.</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    /// <summary>Excel's rules: at most 31 characters, none of []:*?/\, unique ignoring case.</summary>
    private static string UniqueName(string? requested, int index, HashSet<string> used)
    {
        var name = new string((requested ?? string.Empty).Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim().Trim('\'');
        if (name.Length == 0) name = $"Sheet{index + 1}";
        if (name.Length > 31) name = name[..31];
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = (name.Length + suffix.Length > 31 ? name[..(31 - suffix.Length)] : name) + suffix;
        }

        return candidate;
    }

    private static void AddStyles(WorkbookPart workbook)
    {
        var part = workbook.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" })) { Count = 2 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = PatternValues.Solid })) { Count = 3 },
            new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
            new CellFormats(
                new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
                new CellFormat { FontId = 1, FillId = 2, BorderId = 0, ApplyFont = true, ApplyFill = true }) { Count = 2 });
        part.Stylesheet.Save();
    }
}
