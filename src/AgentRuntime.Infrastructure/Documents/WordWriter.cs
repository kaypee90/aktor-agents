using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>Markdown to a Word document with real heading styles, bullet and numbered lists, and tables.</summary>
internal static class WordWriter
{
    private const int BulletNumbering = 1;

    public static byte[] Write(DocumentSpec spec)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            AddStyles(main);
            var numbering = AddNumbering(main);
            var body = main.Document.Body!;

            var blocks = MarkdownBlocks.Parse(spec.Content ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(spec.Title) && blocks.FirstOrDefault() is not HeadingBlock { Level: 1 })
            {
                body.Append(StyledParagraph("Title", [new Span(spec.Title)]));
            }

            int? orderedList = null;
            foreach (var block in blocks)
            {
                if (block is not ListItemBlock { Number: not null }) orderedList = null;
                switch (block)
                {
                    case HeadingBlock h:
                        body.Append(StyledParagraph($"Heading{Math.Min(h.Level, 4)}", h.Spans));
                        break;
                    case ParagraphBlock p:
                        body.Append(StyledParagraph(null, p.Spans));
                        break;
                    case ListItemBlock li:
                        // Each numbered list gets its own numbering instance, so it starts again at 1.
                        var numId = li.Number is null ? BulletNumbering : orderedList ??= NewOrderedList(numbering);
                        var item = StyledParagraph("ListParagraph", li.Spans);
                        item.ParagraphProperties!.Append(new NumberingProperties(new NumberingLevelReference { Val = li.Indent }, new NumberingId { Val = numId }));
                        body.Append(item);
                        break;
                    case QuoteBlock q:
                        body.Append(StyledParagraph("Quote", q.Spans));
                        break;
                    case CodeBlock c:
                        foreach (var line in c.Lines.DefaultIfEmpty(string.Empty)) body.Append(StyledParagraph("Code", [new Span(line, Code: true)]));
                        break;
                    case TableBlock t:
                        body.Append(Table(t));
                        body.Append(new Paragraph());
                        break;
                    case RuleBlock:
                        body.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
                            new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "BBBBBB" }))));
                        break;
                }
            }

            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph StyledParagraph(string? style, IEnumerable<Span> spans)
    {
        var p = new Paragraph(new ParagraphProperties());
        if (style is not null) p.ParagraphProperties!.Append(new ParagraphStyleId { Val = style });
        foreach (var span in spans) p.Append(Run(span));
        return p;
    }

    private static Run Run(Span span)
    {
        // Schema order: rFonts, b, i.
        var props = new RunProperties();
        if (span.Code) props.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
        if (span.Bold) props.Append(new Bold());
        if (span.Italic) props.Append(new Italic());
        var run = new Run();
        if (props.HasChildren) run.Append(props);
        run.Append(new Text(span.Text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static Table Table(TableBlock block)
    {
        var columns = block.Rows.Max(r => r.Count);
        var border = new Func<OpenXmlElement>[]
        {
            () => new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
            () => new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
            () => new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
            () => new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
            () => new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
            () => new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }
        };
        var table = new Table(new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(border.Select(b => b()))));
        table.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (9638 / Math.Max(1, columns)).ToString() })));

        for (var r = 0; r < block.Rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.Append(new TableRowProperties(new TableHeader()));
            for (var c = 0; c < columns; c++)
            {
                var spans = c < block.Rows[r].Count ? block.Rows[r][c] : [new Span(string.Empty)];
                if (r == 0) spans = spans.Select(s => s with { Bold = true }).ToList();
                var cell = new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }));
                if (r == 0) cell.TableCellProperties!.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" });
                cell.Append(StyledParagraph(null, spans));
                row.Append(cell);
            }

            table.Append(row);
        }

        return table;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        Style Paragraph(string id, string name, int sizeHalfPoints, bool bold = false, string? color = null, int before = 0, int after = 120, string? font = null, bool italic = false)
        {
            var run = new StyleRunProperties();
            if (font is not null) run.Append(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
            if (bold) run.Append(new Bold());
            if (italic) run.Append(new Italic());
            if (color is not null) run.Append(new Color { Val = color });
            run.Append(new FontSize { Val = sizeHalfPoints.ToString() });
            // Schema order: name, basedOn, next, qFormat, pPr, rPr.
            var style = new Style(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id };
            if (id != "Normal") style.Append(new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" });
            style.Append(new PrimaryStyle());
            style.Append(new StyleParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() }), run);
            return style;
        }

        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" }, new FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            Paragraph("Normal", "Normal", 22),
            Paragraph("Title", "Title", 52, bold: true, color: "1F2937", after: 240),
            Paragraph("Heading1", "heading 1", 36, bold: true, color: "1F3864", before: 360, after: 120),
            Paragraph("Heading2", "heading 2", 30, bold: true, color: "2F5496", before: 240, after: 80),
            Paragraph("Heading3", "heading 3", 26, bold: true, color: "2F5496", before: 200, after: 60),
            Paragraph("Heading4", "heading 4", 23, bold: true, italic: true, color: "2F5496", before: 160, after: 40),
            Paragraph("ListParagraph", "List Paragraph", 22, after: 40),
            Paragraph("Quote", "Quote", 22, italic: true, color: "555555"),
            Paragraph("Code", "Code", 19, font: "Consolas", after: 0));
        part.Styles.Save();
    }

    private static NumberingDefinitionsPart AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        AbstractNum Abstract(int id, bool ordered) => new(
            Enumerable.Range(0, 5).Select(level => new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = ordered ? NumberFormatValues.Decimal : NumberFormatValues.Bullet },
                new LevelText { Val = ordered ? $"%{level + 1}." : level % 2 == 0 ? "•" : "◦" },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = (720 + 360 * level).ToString(), Hanging = "360" }))
            { LevelIndex = level }))
        { AbstractNumberId = id };

        part.Numbering = new Numbering(
            Abstract(1, ordered: false),
            Abstract(2, ordered: true),
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = BulletNumbering });
        return part;
    }

    /// <summary>A numbering instance that restarts the ordered list at 1.</summary>
    private static int NewOrderedList(NumberingDefinitionsPart part)
    {
        var id = part.Numbering!.Elements<NumberingInstance>().Max(n => n.NumberID!.Value) + 1;
        part.Numbering.Append(new NumberingInstance(
            new AbstractNumId { Val = 2 },
            new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 })
        { NumberID = id });
        return id;
    }
}
