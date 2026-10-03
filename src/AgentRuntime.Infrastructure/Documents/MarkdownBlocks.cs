using System.Text.RegularExpressions;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>A run of text with one style, inside a paragraph, heading, list item or table cell.</summary>
public sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false);

public abstract record Block;
public sealed record HeadingBlock(int Level, IReadOnlyList<Span> Spans) : Block;
public sealed record ParagraphBlock(IReadOnlyList<Span> Spans) : Block;
/// <summary>One list item; <see cref="Number"/> is set for ordered lists.</summary>
public sealed record ListItemBlock(int Indent, int? Number, IReadOnlyList<Span> Spans) : Block;
public sealed record QuoteBlock(IReadOnlyList<Span> Spans) : Block;
public sealed record CodeBlock(IReadOnlyList<string> Lines) : Block;
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<Span>>> Rows) : Block;
public sealed record RuleBlock : Block;

/// <summary>
/// The subset of Markdown agents write in reports, parsed into blocks for the Word and PDF writers:
/// headings, paragraphs, bullet and numbered lists, block quotes, fenced code, tables, rules, and
/// **bold**, *italic* and `code` inline. Links keep their text. Anything else is plain text.
/// </summary>
public static partial class MarkdownBlocks
{
    public static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new ParagraphBlock(Inline(string.Join(" ", paragraph.Select(l => l.Trim())))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence); i++) code.Add(lines[i]);
                blocks.Add(new CodeBlock(code));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (HeadingRegex().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(heading.Groups[1].Length, Inline(heading.Groups[2].Value.TrimEnd('#', ' '))));
                continue;
            }

            if (RuleRegex().IsMatch(trimmed))
            {
                FlushParagraph();
                blocks.Add(new RuleBlock());
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<IReadOnlyList<Span>>> { Cells(trimmed) };
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(Cells(lines[i].Trim()));
                i--;
                blocks.Add(new TableBlock(rows));
                continue;
            }

            if (ListRegex().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                int? number = int.TryParse(item.Groups[2].Value.TrimEnd('.', ')'), out var n) ? n : null;
                blocks.Add(new ListItemBlock(Math.Min(indent, 4), number, Inline(item.Groups[3].Value)));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new QuoteBlock(Inline(trimmed.TrimStart('>', ' '))));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static IReadOnlyList<IReadOnlyList<Span>> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => (IReadOnlyList<Span>)Inline(c.Trim())).ToList();

    /// <summary>Inline styles: **bold** / __bold__, *italic* / _italic_, `code`; [text](url) keeps its text.</summary>
    public static List<Span> Inline(string text)
    {
        text = LinkRegex().Replace(text, "$1");
        var spans = new List<Span>();
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Groups["code"].Success) spans.Add(new Span(m.Groups["code"].Value, Code: true));
            else if (m.Groups["bold"].Success) spans.Add(new Span(m.Groups["bold"].Value, Bold: true));
            else if (m.Groups["bold2"].Success) spans.Add(new Span(m.Groups["bold2"].Value, Bold: true));
            else if (m.Groups["italic"].Success) spans.Add(new Span(m.Groups["italic"].Value, Italic: true));
            else if (m.Groups["italic2"].Success) spans.Add(new Span(m.Groups["italic2"].Value, Italic: true));
            else spans.Add(new Span(m.Value));
        }

        return spans.Count == 0 ? [new Span(string.Empty)] : spans;
    }

    public static string PlainText(IEnumerable<Span> spans) => string.Concat(spans.Select(s => s.Text));

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\*\s*\*\s*\*[\s*]*|-\s*-\s*-[\s-]*|_\s*_\s*_[\s_]*)$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<bold2>.+?)__|\*(?<italic>[^*\s][^*]*?)\*|(?<![\w])_(?<italic2>[^_\s][^_]*?)_(?![\w])|[^`*_]+|[`*_]")]
    private static partial Regex InlineRegex();
}
