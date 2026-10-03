using System.Text;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>What a file is, for reading, previewing and writing it.</summary>
public enum DocumentKind
{
    Text,
    Markdown,
    Csv,
    Html,
    Pdf,
    Word,
    Excel,
    PowerPoint,
    Image,
    /// <summary>A format we recognise but can't read (e.g. legacy .doc/.xls/.ppt, archives, media).</summary>
    Binary
}

/// <summary>File-type detection by extension, with a content sniff for unknown extensions.</summary>
public static class DocumentFormats
{
    private static readonly Dictionary<string, (DocumentKind Kind, string ContentType)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = (DocumentKind.Markdown, "text/markdown"),
        [".markdown"] = (DocumentKind.Markdown, "text/markdown"),
        [".csv"] = (DocumentKind.Csv, "text/csv"),
        [".tsv"] = (DocumentKind.Csv, "text/tab-separated-values"),
        [".html"] = (DocumentKind.Html, "text/html"),
        [".htm"] = (DocumentKind.Html, "text/html"),
        [".pdf"] = (DocumentKind.Pdf, "application/pdf"),
        [".docx"] = (DocumentKind.Word, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = (DocumentKind.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".xlsm"] = (DocumentKind.Excel, "application/vnd.ms-excel.sheet.macroEnabled.12"),
        [".pptx"] = (DocumentKind.PowerPoint, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        [".png"] = (DocumentKind.Image, "image/png"),
        [".jpg"] = (DocumentKind.Image, "image/jpeg"),
        [".jpeg"] = (DocumentKind.Image, "image/jpeg"),
        [".gif"] = (DocumentKind.Image, "image/gif"),
        [".webp"] = (DocumentKind.Image, "image/webp"),
        [".bmp"] = (DocumentKind.Image, "image/bmp"),
        [".svg"] = (DocumentKind.Image, "image/svg+xml"),
        [".doc"] = (DocumentKind.Binary, "application/msword"),
        [".xls"] = (DocumentKind.Binary, "application/vnd.ms-excel"),
        [".ppt"] = (DocumentKind.Binary, "application/vnd.ms-powerpoint"),
        [".zip"] = (DocumentKind.Binary, "application/zip"),
        [".json"] = (DocumentKind.Text, "application/json"),
        [".xml"] = (DocumentKind.Text, "application/xml"),
        [".txt"] = (DocumentKind.Text, "text/plain"),
    };

    /// <summary>Extensions shown as source code, with the language name used for highlighting.</summary>
    private static readonly Dictionary<string, string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".py"] = "python", [".js"] = "javascript", [".jsx"] = "javascript", [".ts"] = "typescript",
        [".tsx"] = "typescript", [".java"] = "java", [".go"] = "go", [".rs"] = "rust", [".rb"] = "ruby", [".php"] = "php",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".hpp"] = "cpp", [".kt"] = "kotlin", [".swift"] = "swift",
        [".sql"] = "sql", [".sh"] = "bash", [".ps1"] = "powershell", [".yaml"] = "yaml", [".yml"] = "yaml",
        [".json"] = "json", [".xml"] = "xml", [".css"] = "css", [".scss"] = "scss", [".toml"] = "toml",
        [".dockerfile"] = "dockerfile", [".tf"] = "hcl", [".ini"] = "ini", [".html"] = "html", [".htm"] = "html"
    };

    /// <summary>Formats create_document writes.</summary>
    public static readonly string[] WritableFormats = ["md", "csv", "docx", "pdf", "xlsx", "pptx"];

    public static DocumentKind KindOf(string path)
    {
        if (ByExtension.TryGetValue(Path.GetExtension(path), out var known)) return known.Kind;
        return LooksLikeText(path) ? DocumentKind.Text : DocumentKind.Binary;
    }

    public static string ContentTypeOf(string path)
    {
        if (ByExtension.TryGetValue(Path.GetExtension(path), out var known)) return known.ContentType;
        return KindOf(path) == DocumentKind.Text ? "text/plain" : "application/octet-stream";
    }

    /// <summary>The highlighting language for source files; null for prose and data.</summary>
    public static string? CodeLanguageOf(string path) => CodeLanguages.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>A file whose first 8 KB are valid UTF-8 without NUL bytes is treated as text.</summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) return true;
            var span = buffer.AsSpan(0, read);
            if (span.Contains((byte)0)) return false;

            // A multi-byte character may be cut at the end of the sample: ignore a short tail.
            var decoder = new UTF8Encoding(false, true);
            for (var trim = 0; trim < 4 && trim < read; trim++)
            {
                try
                {
                    decoder.GetCharCount(span[..(read - trim)]);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    // Try again without the possibly cut character.
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>"12.3 KB" style sizes for people (and for agents reading a file list).</summary>
    public static string HumanSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes / (1024.0 * 1024):0.#} MB";
}
