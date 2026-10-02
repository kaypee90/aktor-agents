using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>Skills through the real API and Postgres: write, edit, upload (.md and .zip), download,
/// turn off, delete; Admin-only changes; other organizations see nothing.</summary>
public sealed class SkillsApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static MultipartFormDataContent FileForm(string fileName, byte[] content, string contentType)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    [Fact]
    public async Task Write_edit_upload_download_and_delete()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("SkillsAdmin", Tenancy.TenantRole.Admin);
        using var api = Host.ClientFor(org);

        // Written in the editor.
        var created = await Json(await api.PostAsJsonAsync("/api/skills", new
        {
            name = "api-style",
            description = "Our REST API conventions. Use when designing or reviewing endpoints.",
            instructions = "Use snake_case. Errors are {\"error\": \"…\"}.",
            files = new[] { new { path = "reference/examples.md", content = "GET /api/tasks" } }
        }));
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/skills", new { name = "api-style", description = "d", instructions = "i" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/skills", new { name = "Bad Name", description = "d", instructions = "i" })).StatusCode);

        var edited = await Json(await api.PutAsJsonAsync("/api/skills/api-style", new { description = "Our REST API conventions.", instructions = "Use snake_case everywhere." }));
        Assert.Equal(2, edited.GetProperty("version").GetInt32());
        Assert.Equal("reference/examples.md", edited.GetProperty("files")[0].GetProperty("path").GetString()); // files kept when not sent

        // Uploaded: a SKILL.md, then a .zip that replaces it as a new version.
        var md = "---\nname: postmortems\ndescription: How we write postmortems.\n---\nTimeline first.";
        var uploaded = await Json(await api.PostAsync("/api/skills/upload", FileForm("SKILL.md", Encoding.UTF8.GetBytes(md), "text/markdown")));
        Assert.Equal("postmortems", uploaded.GetProperty("name").GetString());

        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("postmortems/SKILL.md").Open())) w.Write(md + "\nThen the five whys.");
            using (var w = new StreamWriter(zip.CreateEntry("postmortems/template.md").Open())) w.Write("# Template");
        }

        var replaced = await Json(await api.PostAsync("/api/skills/upload", FileForm("postmortems.zip", zipBuffer.ToArray(), "application/zip")));
        Assert.Equal(2, replaced.GetProperty("version").GetInt32());
        Assert.Equal("template.md", replaced.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/skills/upload", FileForm("x.md", Encoding.UTF8.GetBytes("no frontmatter"), "text/markdown"))).StatusCode);

        var list = await Json(await api.GetAsync("/api/skills"));
        Assert.Equal(["api-style", "postmortems"], list.EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());

        // Download round-trips.
        var download = await api.GetAsync("/api/skills/postmortems/download");
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        using (var zip = new ZipArchive(await download.Content.ReadAsStreamAsync()))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "postmortems/SKILL.md");
        }

        await Json(await api.PatchAsJsonAsync("/api/skills/postmortems", new { enabled = false }));
        Assert.False((await Json(await api.GetAsync("/api/skills/postmortems"))).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/skills/postmortems")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/skills/postmortems")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_change_skills_and_other_organizations_see_nothing()
    {
        if (Skip()) return;
        var admin = await Host.CreateOrganizationAsync("SkillsOwner", Tenancy.TenantRole.Admin);
        var member = await Host.CreateOrganizationAsync("SkillsMember", Tenancy.TenantRole.Member);
        using var adminApi = Host.ClientFor(admin);
        using var memberApi = Host.ClientFor(member);

        await Json(await adminApi.PostAsJsonAsync("/api/skills", new { name = "private-skill", description = "d", instructions = "i" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await memberApi.PostAsJsonAsync("/api/skills", new { name = "x", description = "d", instructions = "i" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberApi.GetAsync("/api/skills/private-skill")).StatusCode);
        Assert.Empty((await Json(await memberApi.GetAsync("/api/skills"))).EnumerateArray());
    }
}
