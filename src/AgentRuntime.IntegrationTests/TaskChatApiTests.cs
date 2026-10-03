using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Infrastructure.Documents;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Task chat through the real API: files attached to a follow-up are saved in the task's workspace,
/// reach the root agent (which reopens the finished task), show in the conversation, and preview
/// as the right kind of document, as do the files agents write.
/// </summary>
public sealed class TaskChatApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
{
    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static MultipartFormDataContent Files(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        return form;
    }

    [Fact]
    public async Task Attached_files_reopen_the_task_reach_the_agent_and_preview_as_documents()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Chat");
        using var api = Host.ClientFor(org);

        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = "Summarize our rental market data." }));
        var taskId = started.GetProperty("task_id").GetString()!;
        Assert.True((await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"))).GetProperty("done").GetBoolean());

        var workbook = DocumentWriter.Write("xlsx", new DocumentSpec
        {
            Sheets = [new SheetSpec("Rents", [["City", "Rent"], ["Accra", "1200"], ["Kumasi", "800"]])]
        });
        var uploaded = await Json(await api.PostAsync($"/api/tasks/{taskId}/attachments",
            Files(("rents.xlsx", workbook), ("../../notes.txt", "Prefer mid-size landlords."u8.ToArray()))));
        var files = uploaded.EnumerateArray().ToList();
        Assert.Equal(["attachments/rents.xlsx", "attachments/notes.txt"], files.Select(f => f.GetProperty("path").GetString()));
        var ids = files.Select(f => f.GetProperty("artifact_id").GetString()!).ToList();

        // An unknown attachment is refused; a real one goes out with the follow-up.
        var bogus = await api.PostAsJsonAsync($"/api/tasks/{taskId}/messages", new { text = "x", attachments = new[] { "nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        var sent = await Json(await api.PostAsJsonAsync($"/api/tasks/{taskId}/messages", new { text = "Use these files too.", attachments = ids }));
        Assert.Equal(2, sent.GetProperty("files").GetArrayLength());

        // The finished root reopened and was told about the files (its standing instructions keep the list).
        var rootId = started.GetProperty("root_agent_id").GetString()!;
        JsonElement root = default;
        for (var i = 0; i < 100; i++)
        {
            root = await Json(await api.GetAsync($"/api/agents/{rootId}"));
            if (root.GetProperty("follow_ups").GetArrayLength() > 0) break;
            await Task.Delay(100);
        }

        Assert.Contains("attachments/rents.xlsx", root.GetProperty("follow_ups")[0].GetString());
        Assert.True((await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"))).GetProperty("done").GetBoolean());

        // The conversation: goal, report, follow-up with its files, the next report.
        JsonElement chat = default;
        for (var i = 0; i < 50; i++)
        {
            chat = await Json(await api.GetAsync($"/api/tasks/{taskId}/chat"));
            if (chat.GetArrayLength() >= 4) break;
            await Task.Delay(100);
        }

        var entries = chat.EnumerateArray().ToList();
        output.WriteLine(chat.ToString());
        Assert.Equal(["user", "agent", "user", "agent"], entries.Take(4).Select(e => e.GetProperty("author").GetString()));
        Assert.Equal(["rents.xlsx", "notes.txt"], entries[2].GetProperty("files").EnumerateArray().Select(f => f.GetProperty("file_name").GetString()));

        // Previews: the workbook as a table, the note as text, and an agent's report as Markdown.
        var table = await Json(await api.GetAsync($"/api/tasks/{taskId}/artifacts/{ids[0]}/preview"));
        Assert.Equal("table", table.GetProperty("kind").GetString());
        Assert.Equal("Accra", table.GetProperty("sheets")[0].GetProperty("rows")[1][0].GetString());
        Assert.Equal("user", table.GetProperty("created_by").GetString());
        var note = await Json(await api.GetAsync($"/api/tasks/{taskId}/artifacts/{ids[1]}/preview"));
        Assert.Equal("text", note.GetProperty("kind").GetString());
        Assert.Equal("Prefer mid-size landlords.", note.GetProperty("text").GetString());

        var artifacts = await Json(await api.GetAsync($"/api/tasks/{taskId}/artifacts"));
        var report = artifacts.EnumerateArray().FirstOrDefault(a => a.GetProperty("file_name").GetString()!.EndsWith(".md"));
        if (report.ValueKind == JsonValueKind.Object)
        {
            var md = await Json(await api.GetAsync($"/api/tasks/{taskId}/artifacts/{report.GetProperty("artifact_id").GetString()}/preview"));
            Assert.Equal("markdown", md.GetProperty("kind").GetString());
        }

        // Downloads are typed, so the dashboard can show PDFs and images from them.
        var content = await api.GetAsync($"/api/tasks/{taskId}/artifacts/{ids[0]}/content");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", content.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_task_can_start_with_files_and_shows_them_on_its_first_message()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Starter");
        using var api = Host.ClientFor(org);

        var deck = DocumentWriter.Write("pptx", new DocumentSpec { Slides = [new SlideSpec("Q3 plan", ["Hire two engineers"])] });
        var staged = (await Json(await api.PostAsync("/api/uploads", Files(("plan.pptx", deck))))).EnumerateArray().Single();
        var uploadId = staged.GetProperty("upload_id").GetString()!;

        // An upload id from another organization (or a made-up one) is refused before any task exists.
        var other = await Host.CreateOrganizationAsync("Stranger");
        using var otherApi = Host.ClientFor(other);
        var stolen = await otherApi.PostAsJsonAsync("/api/tasks", new { goal = "x", attachments = new[] { uploadId } });
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);

        // Files alone are enough to start: the server words the goal.
        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = "", attachments = new[] { uploadId } }));
        var taskId = started.GetProperty("task_id").GetString()!;
        var root = await Json(await api.GetAsync($"/api/agents/{started.GetProperty("root_agent_id").GetString()}"));
        Assert.Contains("attachments/plan.pptx", root.GetProperty("goal").GetString());

        var chat = await Json(await api.GetAsync($"/api/tasks/{taskId}/chat"));
        var goal = chat[0];
        Assert.Equal("Review the attached files and tell me what's important in them.", goal.GetProperty("text").GetString());
        Assert.Equal("plan.pptx", goal.GetProperty("files")[0].GetProperty("file_name").GetString());
        var preview = await Json(await api.GetAsync($"/api/tasks/{taskId}/artifacts/{goal.GetProperty("files")[0].GetProperty("artifact_id").GetString()}/preview"));
        Assert.Equal("slides", preview.GetProperty("kind").GetString());
        Assert.Equal("Q3 plan", preview.GetProperty("slides")[0].GetProperty("title").GetString());

        // The upload was claimed: it can't start a second task.
        var again = await api.PostAsJsonAsync("/api/tasks", new { goal = "again", attachments = new[] { uploadId } });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Files_become_searchable_shared_knowledge()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Knowledge");
        using var api = Host.ClientFor(org);

        var policy = DocumentWriter.Write("docx", new DocumentSpec { Content = "# Refund policy\n\nAnnual plans are refundable within 30 days." });
        var added = (await Json(await api.PostAsync("/api/memory/files",
            Files(("refund-policy.docx", policy), ("logo.png", [0x89, 0x50, 0x4E, 0x47, 0, 0]))))).EnumerateArray().ToList();
        Assert.Equal(1, added[0].GetProperty("entries").GetInt32());
        Assert.Equal(JsonValueKind.Null, added[0].GetProperty("error").ValueKind);
        Assert.Equal(0, added[1].GetProperty("entries").GetInt32());
        Assert.Contains("Image", added[1].GetProperty("error").GetString());

        var found = await Json(await api.GetAsync("/api/memory?q=refundable"));
        var entry = found.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "refund-policy.docx");
        Assert.Contains("refundable within 30 days", entry.GetProperty("value").GetString());
        Assert.Equal("user", entry.GetProperty("agent_id").GetString());
    }

    [Fact]
    public void Knowledge_passages_are_cut_at_paragraphs_and_never_exceed_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i}. " + new string('x', 300))) +
                   "\n\n" + string.Join(" ", Enumerable.Repeat("word", 3000));
        var chunks = AgentRuntime.Api.Controllers.MemoryController.Chunk(text, 4000);

        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 4000));
        Assert.StartsWith("Paragraph 1.", chunks[0]);
        Assert.EndsWith(new string('x', 300), chunks[0]); // a paragraph is never split across passages
        Assert.Equal(text.Replace("\n", "").Replace(" ", "").Length, string.Concat(chunks).Replace("\n", "").Replace(" ", "").Length);
        Assert.Single(AgentRuntime.Api.Controllers.MemoryController.Chunk("short", 4000));
    }

    [Fact]
    public async Task A_task_that_stopped_partway_continues_with_a_bigger_budget()
    {
        if (Skip()) return;
        var org = await Host.CreateOrganizationAsync("Continue");
        using var api = Host.ClientFor(org);

        var started = await Json(await api.PostAsJsonAsync("/api/tasks", new { goal = "Summarize our rental market data.", budget = new { max_tokens = 40_000 } }));
        var taskId = started.GetProperty("task_id").GetString()!;
        var rootId = started.GetProperty("root_agent_id").GetString()!;
        // Still running: it takes messages, not a continue.
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync($"/api/tasks/{taskId}/continue", new { })).StatusCode);
        Assert.True((await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"))).GetProperty("done").GetBoolean());

        // Record the run as stopped partway, as the runtime does when a budget runs out: the root's
        // completion event and the task result both carry what was left. (The demo model always
        // finishes; BudgetWrapUpTests covers the runtime reporting a partial result itself.)
        var left = new[] { "Compare rents in Kumasi", "Write the pricing section" };
        using (var scope = Host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentRuntime.Infrastructure.Persistence.AgentDbContext>();
            var row = await db.Tasks.FindAsync(taskId);
            row!.ResultJson = JsonSerializer.Serialize(new { status = "partial", summary = "Got halfway.", unresolved_items = left });
            db.Events.Add(new AgentRuntime.Infrastructure.Persistence.EventRecord
            {
                TenantId = row.TenantId,
                EventId = Guid.NewGuid().ToString("n"),
                Type = "AgentCompleted",
                Timestamp = DateTimeOffset.UtcNow,
                AgentId = rootId,
                TaskId = taskId,
                Summary = "Agent 'Root' completed its goal.",
                DataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["status"] = "partial",
                    ["summary"] = "Got halfway.",
                    ["remaining_work"] = JsonSerializer.Serialize(left)
                })
            });
            await db.SaveChangesAsync();
        }

        var report = (await Json(await api.GetAsync($"/api/tasks/{taskId}/chat"))).EnumerateArray().Last();
        Assert.Equal("partial", report.GetProperty("status").GetString());
        Assert.Equal(left, report.GetProperty("remaining_work").EnumerateArray().Select(r => r.GetString()));

        var task = await Json(await api.GetAsync($"/api/tasks/{taskId}"));
        Assert.Equal(40_000, task.GetProperty("budget").GetProperty("max_tokens").GetInt32());
        Assert.True(task.GetProperty("budget_ceiling").GetProperty("max_cost_usd").GetDecimal() > 0);

        // Continue with room to work: the root is told what was left, and finishes.
        var sent = await Json(await api.PostAsJsonAsync($"/api/tasks/{taskId}/continue",
            new { budget = new { max_tokens = 200_000, max_cost_usd = 5 }, note = "Keep it short." }));
        var text = sent.GetProperty("text").GetString()!;
        Assert.Contains("What was left:\n- Compare rents in Kumasi\n- Write the pricing section", text);
        Assert.Contains("Keep it short.", text);

        // The new round's budget is on top of what the root had spent.
        JsonElement root = default;
        for (var i = 0; i < 100; i++)
        {
            root = await Json(await api.GetAsync($"/api/agents/{rootId}"));
            if (root.GetProperty("follow_ups").GetArrayLength() > 0) break;
            await Task.Delay(100);
        }

        // Set at reopen to what was spent and reserved then, plus the round's 200,000. The agent may
        // have spent more since, so what's spent now is an upper bound for that base.
        var usage = root.GetProperty("usage");
        var granted = root.GetProperty("budget").GetProperty("max_tokens").GetInt32() - 200_000;
        Assert.InRange(granted, 0, usage.GetProperty("tokens_used").GetInt32() + usage.GetProperty("reserved_tokens").GetInt32());

        Assert.True((await Json(await api.GetAsync($"/api/tasks/{taskId}/wait?timeout_seconds=90"))).GetProperty("done").GetBoolean());
        var last = (await Json(await api.GetAsync($"/api/tasks/{taskId}/chat"))).EnumerateArray().Last();
        for (var i = 0; i < 50 && last.GetProperty("author").GetString() != "agent"; i++)
        {
            await Task.Delay(100);
            last = (await Json(await api.GetAsync($"/api/tasks/{taskId}/chat"))).EnumerateArray().Last();
        }

        Assert.Equal("completed", last.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Attachments_are_refused_for_another_organizations_task()
    {
        if (Skip()) return;
        var owner = await Host.CreateOrganizationAsync("Owner");
        var other = await Host.CreateOrganizationAsync("Other");
        using var ownerApi = Host.ClientFor(owner);
        using var otherApi = Host.ClientFor(other);

        var started = await Json(await ownerApi.PostAsJsonAsync("/api/tasks", new { goal = "Summarize our rental market data." }));
        var taskId = started.GetProperty("task_id").GetString()!;

        var response = await otherApi.PostAsync($"/api/tasks/{taskId}/attachments", Files(("x.txt", "hi"u8.ToArray())));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherApi.GetAsync($"/api/tasks/{taskId}/chat")).StatusCode);
    }
}
