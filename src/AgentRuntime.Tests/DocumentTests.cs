using System.Text.Json;
using AgentRuntime.Events;
using AgentRuntime.Infrastructure.Documents;
using AgentRuntime.Infrastructure.Tools;
using AgentRuntime.Tools;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Tests;

/// <summary>
/// Documents agents write (create_document) and files users attach: every Office file the writers
/// produce is valid against the Open XML schema, and every format reads back as the text it holds.
/// </summary>
public sealed class DocumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aktor-docs-" + Guid.NewGuid().ToString("n")[..8]);

    public DocumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Report = """
        # Market analysis

        The market for **AI property management** is growing *fast*. See `rent_roll.csv`.

        ## Key findings

        - Demand is strong in mid-size portfolios
        - Incumbents are slow
          - Especially on AI features
        1. Validate pricing
        2. Build the MVP

        | Competitor | Price | Users |
        | --- | --- | --- |
        | AppFolio | 1.40 | 20000 |
        | Buildium | 1.00 | 15000 |

        > Customers want automation.

        ```
        SELECT * FROM leases;
        ```

        ---

        Café prices — “quoted” • done.
        """;

    private async Task<string> WriteAsync(string name, DocumentSpec spec)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, DocumentWriter.Write(DocumentWriter.FormatOf(path)!, spec));
        return path;
    }

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator().Validate(package).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public async Task Word_documents_are_valid_and_keep_headings_lists_and_tables()
    {
        var path = await WriteAsync("report.docx", new DocumentSpec { Title = "Ignored: content has its own title", Content = Report });
        using (var doc = WordprocessingDocument.Open(path, false)) AssertValid(doc);

        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(DocumentKind.Word, read.Kind);
        Assert.Contains("# Market analysis", read.Text);
        Assert.Contains("## Key findings", read.Text);
        Assert.Contains("- Demand is strong in mid-size portfolios", read.Text);
        Assert.Contains("| AppFolio | 1.40 | 20000 |", read.Text);
        Assert.Contains("AI property management", read.Text);
        Assert.DoesNotContain("**", read.Text);
    }

    [Fact]
    public async Task Excel_workbooks_are_valid_with_typed_cells_and_safe_sheet_names()
    {
        var path = await WriteAsync("data.xlsx", new DocumentSpec
        {
            Sheets =
            [
                new SheetSpec("Pricing: 2026/Q1", [["Plan", "Price", "Code"], ["Starter", "29.5", "007"], ["Pro", "99", "X1"]]),
                new SheetSpec("Pricing: 2026/Q1", [["Only"], ["one"]])
            ]
        });
        using (var doc = SpreadsheetDocument.Open(path, false)) AssertValid(doc);

        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(2, read.Sheets!.Count);
        Assert.Equal("Pricing 2026Q1", read.Sheets[0].Name);
        Assert.Equal("Pricing 2026Q1 (2)", read.Sheets[1].Name);
        Assert.Equal(["Starter", "29.5", "007"], read.Sheets[0].Rows[1]);
        Assert.Contains("| Pro | 99 | X1 |", read.Text);
    }

    [Fact]
    public async Task Excel_sheets_come_from_markdown_tables_when_none_are_given()
    {
        var path = await WriteAsync("from-content.xlsx", new DocumentSpec { Title = "Competitors", Content = Report });
        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal("Competitors", Assert.Single(read.Sheets!).Name);
        Assert.Equal(["Competitor", "Price", "Users"], read.Sheets![0].Rows[0]);
        Assert.Equal(3, read.Sheets[0].Rows.Count);
    }

    [Fact]
    public async Task PowerPoint_decks_are_valid_and_split_long_slides()
    {
        var bullets = Enumerable.Range(1, 15).Select(i => $"Point {i} & more").ToList();
        var path = await WriteAsync("deck.pptx", new DocumentSpec
        {
            Title = "Feasibility <review>",
            Slides = [new SlideSpec("Findings", bullets), new SlideSpec("Next steps", ["Ship it"])]
        });
        using (var doc = PresentationDocument.Open(path, false)) AssertValid(doc);

        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(4, read.Slides!.Count); // title, findings, findings (continued), next steps
        Assert.Equal("Feasibility <review>", read.Slides[0].Title);
        Assert.Equal("Findings", read.Slides[1].Title);
        Assert.Equal("Findings (continued)", read.Slides[2].Title);
        Assert.Contains("Point 15 & more", read.Slides[2].Paragraphs);
        Assert.Equal(["Ship it"], read.Slides[3].Paragraphs);
    }

    [Fact]
    public async Task PowerPoint_slides_come_from_markdown_sections_when_none_are_given()
    {
        var path = await WriteAsync("from-content.pptx", new DocumentSpec { Content = Report });
        using (var doc = PresentationDocument.Open(path, false)) AssertValid(doc);
        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(["Market analysis", "Key findings"], read.Slides!.Select(s => s.Title));
        Assert.Contains("Validate pricing", read.Slides[1].Paragraphs);
    }

    [Fact]
    public async Task Pdfs_hold_the_text_including_tables_long_paragraphs_and_accents()
    {
        var longParagraph = string.Join(" ", Enumerable.Repeat("Lorem ipsum dolor sit amet consectetur.", 400));
        var path = await WriteAsync("report.pdf", new DocumentSpec { Content = Report + "\n\n" + longParagraph + "\n\nEmoji 🚀 end" });

        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(DocumentKind.Pdf, read.Kind);
        Assert.Contains("Page 2", read.Text); // the long paragraph wrapped onto more pages
        Assert.Contains("Market analysis", read.Text);
        Assert.Contains("AppFolio", read.Text);
        Assert.Contains("SELECT * FROM leases;", read.Text);
        // Accents need the embedded DejaVu fonts; without them they're dropped, never drawn as another letter.
        Assert.Contains(File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf") ? "Café" : "Cafe", read.Text);
        Assert.Contains("“quoted” • done", read.Text);
        Assert.Contains("Emoji ?", read.Text); // no font here draws emoji
        Assert.Null(read.Note);
    }

    [Fact]
    public async Task Plain_english_pdfs_embed_no_fonts()
    {
        var path = await WriteAsync("small.pdf", new DocumentSpec { Content = "# Title\n\nJust “plain” text • here." });
        Assert.True(new FileInfo(path).Length < 20_000, $"{new FileInfo(path).Length} bytes");
        Assert.Contains("Just “plain” text • here.", (await DocumentReader.ReadAsync(path)).Text);
    }

    [Fact]
    public async Task Csv_quotes_fields_and_parses_back()
    {
        var path = await WriteAsync("rows.csv", new DocumentSpec { Sheets = [new SheetSpec("x", [["name", "note"], ["Acme, Inc.", "said \"hi\"\nthen left"]])] });
        var read = await DocumentReader.ReadAsync(path);
        Assert.Equal(["Acme, Inc.", "said \"hi\"\nthen left"], read.Sheets![0].Rows[1]);
    }

    [Fact]
    public async Task Images_binaries_and_damaged_files_get_a_note_not_an_exception()
    {
        var png = Path.Combine(_dir, "pic.png");
        await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 0, 0, 0]);
        var blob = Path.Combine(_dir, "data.bin");
        await File.WriteAllBytesAsync(blob, [0, 1, 2, 3, 0, 255]);
        var broken = Path.Combine(_dir, "broken.docx");
        await File.WriteAllTextAsync(broken, "not a zip");
        var text = Path.Combine(_dir, "notes.unknownext");
        await File.WriteAllTextAsync(text, "plain words");

        Assert.Equal(DocumentKind.Image, (await DocumentReader.ReadAsync(png)).Kind);
        Assert.Contains("Binary file", (await DocumentReader.ReadAsync(blob)).Note);
        Assert.Contains("couldn't be read", (await DocumentReader.ReadAsync(broken)).Note);
        Assert.Equal("plain words", (await DocumentReader.ReadAsync(text)).Text);
    }

    // ---- The tools agents use ----

    private sealed class NoEvents : IEventPublisher
    {
        public List<RuntimeEvent> Published { get; } = [];

        public ValueTask PublishAsync(RuntimeEvent evt, CancellationToken cancellationToken = default)
        {
            Published.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

    private ToolExecutionRequest Request(object args) => new()
    {
        AgentId = "agent-1",
        TaskId = "task1",
        ToolName = "create_document",
        ArgumentsJson = JsonSerializer.Serialize(args)
    };

    [Fact]
    public async Task Create_document_writes_the_format_records_the_artifact_and_filesystem_read_reads_it_back()
    {
        var options = Options.Create(new ToolsOptions { WorkspaceRoot = _dir });
        var events = new NoEvents();
        var create = new CreateDocumentTool(options, events);

        // Numbers in cells and a format that disagrees with the extension.
        var result = await create.ExecuteAsync(Request(new
        {
            path = "out/budget.txt",
            format = "excel",
            sheets = new object[] { new { name = "Budget", rows = new object[] { new object[] { "Item", "Cost" }, new object[] { "Servers", 1200.5 } } } }
        }));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("out/budget.xlsx", result.ResultJson);
        var artifact = Assert.Single(events.Published);
        Assert.EndsWith("budget.xlsx", artifact.Data["location"]);
        Assert.Equal("Data", artifact.Data["type"]);

        var read = await new FilesystemReadTool(options).ExecuteAsync(Request(new { path = "out/budget.xlsx" }) with { ToolName = "filesystem_read" });
        Assert.True(read.Success);
        Assert.Contains("| Servers | 1200.5 |", JsonDocument.Parse(read.ResultJson).RootElement.GetProperty("content").GetString());

        Assert.False((await create.ExecuteAsync(Request(new { path = "x.docx" }))).Success); // nothing to write
        Assert.False((await create.ExecuteAsync(Request(new { path = "x.exe", content = "hi" }))).Success); // unknown format
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => create.ExecuteAsync(Request(new { path = "../../escape.md", content = "hi" })));
    }
}
