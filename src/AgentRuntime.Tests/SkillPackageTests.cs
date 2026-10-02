using System.IO.Compression;
using System.Text;
using AgentRuntime.Skills;

namespace AgentRuntime.Tests;

/// <summary>Reading and validating skills, whether written in the editor or uploaded.</summary>
public sealed class SkillPackageTests
{
    private const string Valid = """
        ---
        name: incident-postmortems
        description: How we write postmortems. Use when asked for a postmortem: timeline, impact, causes.
        ---

        ## Steps
        1. Build the timeline.
        """;

    private static MemoryStream Zip(params (string Path, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Markdown_frontmatter_gives_name_description_and_instructions()
    {
        var skill = SkillPackage.FromMarkdown(Valid);
        Assert.Equal("incident-postmortems", skill.Name);
        Assert.Equal("How we write postmortems. Use when asked for a postmortem: timeline, impact, causes.", skill.Description);
        Assert.StartsWith("## Steps", skill.Instructions);
        Assert.Empty(skill.Files);
    }

    [Theory]
    [InlineData("no frontmatter at all", "frontmatter")]
    [InlineData("---\ndescription: d\n---\nbody", "name")]
    [InlineData("---\nname: Bad_Name\ndescription: d\n---\nbody", "lowercase")]
    [InlineData("---\nname: ok\n---\nbody", "description")]
    [InlineData("---\nname: ok\ndescription: d\n---\n   ", "no instructions")]
    public void Invalid_skills_are_refused_with_a_reason(string markdown, string reason)
    {
        var ex = Assert.Throws<SkillPackageException>(() => SkillPackage.FromMarkdown(markdown));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zip_with_skill_in_a_folder_keeps_text_files_and_drops_the_rest()
    {
        using var zip = Zip(
            ("incident-postmortems/SKILL.md", Text(Valid)),
            ("incident-postmortems/reference/template.md", Text("# Template")),
            ("incident-postmortems/scripts/timeline.py", Text("print('hi')")),
            ("incident-postmortems/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]),
            ("incident-postmortems/.DS_Store", Text("junk")),
            ("__MACOSX/incident-postmortems/._SKILL.md", Text("junk")));

        var skill = SkillPackage.FromZip(zip);
        Assert.Equal("incident-postmortems", skill.Name);
        Assert.Equal(["reference/template.md", "scripts/timeline.py"], skill.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Zip_without_SKILL_md_or_not_a_zip_is_refused()
    {
        using var noManifest = Zip(("readme.md", Text("hi")));
        Assert.Contains("SKILL.md", Assert.Throws<SkillPackageException>(() => SkillPackage.FromZip(noManifest)).Message);
        using var garbage = new MemoryStream(Text("not a zip"));
        Assert.Contains("zip", Assert.Throws<SkillPackageException>(() => SkillPackage.FromZip(garbage)).Message);
    }

    [Fact]
    public void Zip_size_and_file_limits_apply()
    {
        using var big = Zip(("SKILL.md", Text(Valid)), ("big.txt", Text(new string('x', 5000))));
        Assert.Throws<SkillPackageException>(() => SkillPackage.FromZip(big, new SkillOptions { MaxTotalBytes = 1000 }));
        using var many = Zip([("SKILL.md", Text(Valid)), .. Enumerable.Range(0, 5).Select(i => ($"f{i}.md", Text("x")))]);
        Assert.Throws<SkillPackageException>(() => SkillPackage.FromZip(many, new SkillOptions { MaxFiles = 3 }));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("a/../../escape.md")]
    [InlineData("C:/windows/x.md")]
    [InlineData("")]
    public void Paths_that_escape_the_skill_are_refused(string path)
    {
        Assert.Null(SkillPackage.NormalizePath(path));
        Assert.Throws<SkillPackageException>(() => SkillPackage.FromParts("ok", "d", "body", [new SkillFile(path, "x")]));
    }

    [Fact]
    public void Written_skills_follow_the_same_rules_and_round_trip_through_a_zip()
    {
        var written = SkillPackage.FromParts("api-style", "Our REST API conventions: use when designing endpoints.", "Use snake_case.",
            [new SkillFile("./reference/errors.md", "Errors are {error}.")]);
        Assert.Equal("reference/errors.md", written.Files.Single().Path);
        Assert.Throws<SkillPackageException>(() => SkillPackage.FromParts("API Style", "d", "body", null));
        Assert.Throws<SkillPackageException>(() => SkillPackage.FromParts("ok", "d", "body", [new SkillFile("SKILL.md", "x")]));

        using var zip = new MemoryStream(SkillPackage.ToZip(written));
        var back = SkillPackage.FromZip(zip);
        Assert.Equal(written.Name, back.Name);
        Assert.Equal(written.Description, back.Description);
        Assert.Equal(written.Instructions, back.Instructions);
        Assert.Equal(written.Files, back.Files);
    }
}
