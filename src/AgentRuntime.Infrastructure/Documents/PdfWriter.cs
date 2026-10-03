using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Font = UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>
/// Markdown to an A4 PDF: headings, wrapped paragraphs, lists, block quotes, code, tables with
/// borders, rules and page numbers. Plain English text uses the PDF standard fonts (nothing
/// embedded, small files). Text with accents or other scripts uses DejaVu fonts when they are
/// installed (the API image installs them), embedded in the file; without them, accents are
/// dropped (é → e) and other characters are written as '?'.
/// </summary>
internal sealed partial class PdfWriter
{
    private const double PageWidth = 595.28, PageHeight = 841.89, Margin = 56;
    private const double ContentWidth = PageWidth - 2 * Margin;

    private readonly PdfDocumentBuilder _builder = new();
    private readonly List<PdfPageBuilder> _pages = [];
    private readonly Font _regular, _bold, _italic, _boldItalic, _mono;
    private readonly Dictionary<char, bool> _supported = [];
    private readonly Dictionary<(Font, double, string), double> _widths = [];
    private PdfPageBuilder _page = null!;
    private double _y;

    /// <summary>Embedded TrueType fonts in use: then any character the font has can be drawn.</summary>
    private readonly bool _trueType;

    /// <summary>The non-ASCII characters the standard fonts draw correctly (their StandardEncoding).</summary>
    private const string StandardExtras = "‘’“”‚„–—•…†‡‰‹›«»¡¢£¥¤§¶·¿ßæÆøØœŒłŁıƒªº´`ˆ˜¯˘˙¨˚¸˝˛ˇ";

    private static readonly Lazy<Dictionary<string, byte[]>?> DejaVu = new(LoadDejaVu);

    private PdfWriter(bool trueType)
    {
        if (trueType && DejaVu.Value is { } fonts)
        {
            _trueType = true;
            Font Load(string name, string fallback) => _builder.AddTrueTypeFont(fonts.GetValueOrDefault(name) ?? fonts[fallback]);
            _regular = Load("DejaVuSans.ttf", "DejaVuSans.ttf");
            _bold = Load("DejaVuSans-Bold.ttf", "DejaVuSans.ttf");
            _italic = Load("DejaVuSans-Oblique.ttf", "DejaVuSans.ttf");
            _boldItalic = Load("DejaVuSans-BoldOblique.ttf", "DejaVuSans-Bold.ttf");
            // PdfPig can't subset DejaVu Sans Mono; code is nearly always ASCII, which Courier draws.
            _mono = _builder.AddStandard14Font(Standard14Font.Courier);
        }
        else
        {
            _regular = _builder.AddStandard14Font(Standard14Font.Helvetica);
            _bold = _builder.AddStandard14Font(Standard14Font.HelveticaBold);
            _italic = _builder.AddStandard14Font(Standard14Font.HelveticaOblique);
            _boldItalic = _builder.AddStandard14Font(Standard14Font.HelveticaBoldOblique);
            _mono = _builder.AddStandard14Font(Standard14Font.Courier);
        }

        NewPage();
    }

    /// <summary>DejaVu from PDF_FONT_DIR or the usual Linux font folders; null when not installed.</summary>
    private static Dictionary<string, byte[]>? LoadDejaVu()
    {
        string[] dirs = [Environment.GetEnvironmentVariable("PDF_FONT_DIR") ?? string.Empty,
            "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF", "/usr/local/share/fonts"];
        string[] names = ["DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf"];
        foreach (var dir in dirs.Where(d => d.Length > 0 && File.Exists(Path.Combine(d, "DejaVuSans.ttf"))))
        {
            var fonts = names.Where(n => File.Exists(Path.Combine(dir, n))).ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(dir, n)));
            using var probe = new PdfDocumentBuilder();
            if (probe.CanUseTrueTypeFont(fonts["DejaVuSans.ttf"], out _)) return fonts;
        }

        return null;
    }

    public static byte[] Write(DocumentSpec spec)
    {
        // Only embed fonts (a few MB) when the text needs characters the standard fonts lack.
        var allText = (spec.Title ?? string.Empty) + (spec.Content ?? string.Empty);
        var writer = new PdfWriter(trueType: allText.Any(c => c > 127 && !StandardExtras.Contains(c)));
        var blocks = MarkdownBlocks.Parse(spec.Content ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(spec.Title) && blocks.FirstOrDefault() is not HeadingBlock { Level: 1 })
        {
            writer.Text([new Span(spec.Title, Bold: true)], 22, 0, before: 0, after: 14);
        }

        writer.Blocks(blocks);
        return writer.Finish();
    }

    private void Blocks(List<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    var (size, before) = h.Level switch { 1 => (20.0, 18.0), 2 => (16.0, 14.0), 3 => (13.5, 10.0), _ => (12.0, 8.0) };
                    Text(h.Spans.Select(s => s with { Bold = true }).ToList(), size, 0, before, after: 6, keepWithNext: true);
                    break;
                case ParagraphBlock p:
                    Text(p.Spans, 10.5, 0, before: 0, after: 7);
                    break;
                case ListItemBlock li:
                    var indent = 14 + li.Indent * 16;
                    var marker = li.Number is { } n ? $"{n}." : "•";
                    Text(li.Spans, 10.5, indent, before: 0, after: 3, marker: marker);
                    break;
                case QuoteBlock q:
                    _page.SetTextAndFillColor(90, 90, 90);
                    Text(q.Spans.Select(s => s with { Italic = true }).ToList(), 10.5, 14, before: 2, after: 8);
                    _page.SetTextAndFillColor(0, 0, 0);
                    break;
                case CodeBlock c:
                    Code(c.Lines);
                    break;
                case TableBlock t:
                    Table(t);
                    break;
                case RuleBlock:
                    Ensure(14);
                    _y -= 6;
                    _page.DrawLine(new PdfPoint(Margin, _y), new PdfPoint(PageWidth - Margin, _y), 0.6);
                    _y -= 8;
                    break;
            }
        }
    }

    private byte[] Finish()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            var label = $"Page {i + 1} of {_pages.Count}";
            _pages[i].AddText(label, 8.5, new PdfPoint(PageWidth / 2 - Width(label, _regular, 8.5) / 2, Margin / 2), _regular);
        }

        return _builder.Build();
    }

    private void NewPage()
    {
        _page = _builder.AddPage(PageSize.A4);
        _pages.Add(_page);
        _y = PageHeight - Margin;
    }

    private void Ensure(double height)
    {
        if (_y - height < Margin) NewPage();
    }

    // ---- Text ---------------------------------------------------------------------------------

    private sealed record Piece(string Text, Font Font, bool SpaceBefore);

    /// <summary>Writes spans wrapped to the content width, from <paramref name="indent"/>.</summary>
    private void Text(IReadOnlyList<Span> spans, double size, double indent, double before, double after,
        string? marker = null, bool keepWithNext = false)
    {
        var lineHeight = size * 1.35;
        var lines = Wrap(Pieces(spans), size, ContentWidth - indent);
        _y -= before;
        // A heading keeps a couple of lines' room below it, so it doesn't end a page.
        Ensure(lineHeight * (keepWithNext ? 3 : 1));

        for (var i = 0; i < lines.Count; i++)
        {
            Ensure(lineHeight);
            _y -= size;
            if (i == 0 && marker is not null)
            {
                _page.AddText(marker, size, new PdfPoint(Margin + indent - Width(marker, _regular, size) - 5, _y), _regular);
            }

            var x = Margin + indent;
            foreach (var (piece, j) in lines[i].Select((p, j) => (p, j)))
            {
                if (piece.SpaceBefore && j > 0) x += Width(" ", piece.Font, size);
                _page.AddText(piece.Text, size, new PdfPoint(x, _y), piece.Font);
                x += Width(piece.Text, piece.Font, size);
            }

            _y -= lineHeight - size;
        }

        _y -= after;
    }

    private List<Piece> Pieces(IEnumerable<Span> spans)
    {
        var pieces = new List<Piece>();
        var pendingSpace = false;
        foreach (var span in spans)
        {
            var font = span.Code ? _mono : span.Bold && span.Italic ? _boldItalic : span.Bold ? _bold : span.Italic ? _italic : _regular;
            foreach (Match m in TokenRegex().Matches(Clean(span.Text, standardFont: font == _mono)))
            {
                if (string.IsNullOrWhiteSpace(m.Value)) { pendingSpace = true; continue; }
                pieces.Add(new Piece(m.Value, font, pendingSpace));
                pendingSpace = false;
            }
        }

        return pieces;
    }

    private List<List<Piece>> Wrap(List<Piece> pieces, double size, double width)
    {
        var lines = new List<List<Piece>>();
        var line = new List<Piece>();
        var used = 0.0;
        foreach (var original in pieces)
        {
            var piece = original;
            var space = line.Count > 0 && piece.SpaceBefore ? Width(" ", piece.Font, size) : 0;
            var w = Width(piece.Text, piece.Font, size);
            if (line.Count > 0 && used + space + w > width)
            {
                lines.Add(line);
                line = [];
                used = 0;
                space = 0;
            }

            // A word wider than the line is broken across lines.
            while (w > width && piece.Text.Length > 1)
            {
                var fit = piece.Text.Length - 1;
                while (fit > 1 && Width(piece.Text[..fit], piece.Font, size) > width - used) fit--;
                line.Add(piece with { Text = piece.Text[..fit] });
                lines.Add(line);
                line = [];
                used = 0;
                piece = piece with { Text = piece.Text[fit..], SpaceBefore = false };
                w = Width(piece.Text, piece.Font, size);
            }

            line.Add(piece);
            used += space + w;
        }

        if (line.Count > 0 || lines.Count == 0) lines.Add(line);
        return lines;
    }

    private double Width(string text, Font font, double size)
    {
        if (text.Length == 0) return 0;
        if (_widths.TryGetValue((font, size, text), out var cached)) return cached;
        var letters = _page.MeasureText(text, size, new PdfPoint(0, 0), font);
        var width = letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X;
        if (_widths.Count < 20_000) _widths[(font, size, text)] = width;
        return width;
    }

    /// <summary>Characters the fonts can't draw lose their accent (é → e) or become '?'; tabs become spaces.</summary>
    private string Clean(string text, bool standardFont = false)
    {
        // Whole characters, so an emoji (two UTF-16 units) becomes one '?'.
        var chars = text.Replace("\t", "    ").EnumerateRunes().Where(r => !System.Text.Rune.IsControl(r)).Select(r =>
        {
            if (!r.IsBmp) return '?';
            var c = (char)r.Value;
            if (Supported(c, standardFont)) return c;
            var bare = r.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
            return bare != c && Supported(bare, standardFont) ? bare : '?';
        });
        return new string(chars.ToArray());
    }

    private bool Supported(char c, bool standardFont = false)
    {
        if (c < 128) return true;
        if (!_trueType || standardFont) return StandardExtras.Contains(c);
        if (_supported.TryGetValue(c, out var ok)) return ok;
        try
        {
            ok = !char.IsSurrogate(c) && _page.MeasureText(c.ToString(), 10, new PdfPoint(0, 0), _regular).Count > 0;
        }
        catch (Exception)
        {
            ok = false;
        }

        return _supported[c] = ok;
    }

    // ---- Code and tables ----------------------------------------------------------------------

    private void Code(IReadOnlyList<string> lines)
    {
        const double size = 9, lineHeight = 12;
        var charWidth = Width("M", _mono, size);
        var perLine = Math.Max(10, (int)((ContentWidth - 12) / charWidth));
        _y -= 2;
        foreach (var raw in lines.DefaultIfEmpty(string.Empty))
        {
            var line = Clean(raw, standardFont: true);
            do
            {
                var part = line.Length > perLine ? line[..perLine] : line;
                line = line.Length > perLine ? line[perLine..] : string.Empty;
                Ensure(lineHeight);
                _y -= lineHeight;
                if (part.Length > 0) _page.AddText(part, size, new PdfPoint(Margin + 8, _y + 3), _mono);
            }
            while (line.Length > 0);
        }

        _y -= 8;
    }

    private void Table(TableBlock table)
    {
        const double size = 9.5, lineHeight = 12.5, pad = 4;
        var columns = table.Rows.Max(r => r.Count);
        if (columns == 0) return;
        var colWidth = ContentWidth / columns;
        _y -= 4;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = Enumerable.Range(0, columns).Select(c =>
            {
                var spans = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                if (r == 0) spans = spans.Select(s => s with { Bold = true }).ToList();
                return Wrap(Pieces(spans), size, colWidth - 2 * pad);
            }).ToList();
            var height = Math.Min(cells.Max(c => c.Count) * lineHeight + 2 * pad, PageHeight - 2 * Margin);

            if (_y - height < Margin)
            {
                NewPage();
            }

            var top = _y;
            if (r == 0 || Math.Abs(top - (PageHeight - Margin)) < 0.01)
            {
                _page.DrawLine(new PdfPoint(Margin, top), new PdfPoint(PageWidth - Margin, top), 0.5);
            }

            for (var c = 0; c < columns; c++)
            {
                var y = top - pad;
                foreach (var line in cells[c])
                {
                    y -= size;
                    if (y < Margin) break;
                    var x = Margin + c * colWidth + pad;
                    foreach (var (piece, j) in line.Select((p, j) => (p, j)))
                    {
                        if (piece.SpaceBefore && j > 0) x += Width(" ", piece.Font, size);
                        _page.AddText(piece.Text, size, new PdfPoint(x, y), piece.Font);
                        x += Width(piece.Text, piece.Font, size);
                    }

                    y -= lineHeight - size;
                }
            }

            _y = top - height;
            _page.DrawLine(new PdfPoint(Margin, _y), new PdfPoint(PageWidth - Margin, _y), r == 0 ? 0.9 : 0.4);
            for (var c = 0; c <= columns; c++)
            {
                var x = Margin + c * colWidth;
                _page.DrawLine(new PdfPoint(x, top), new PdfPoint(x, _y), 0.4);
            }
        }

        _y -= 10;
    }

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex TokenRegex();
}
