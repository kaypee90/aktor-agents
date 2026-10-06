using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.IntegrationTests.TestSupport;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>
/// Editing a pipeline by hand on the canvas, through the real API: stages placed by dragging keep
/// their place without making versions, connections are drawn and removed, a dropped agent lands
/// where it was dropped, and a stage's model must be one the organization has. Plus @mentions in
/// the plain-language editor, on the Mock provider.
/// </summary>
public sealed class PipelineCanvasApiTests(ApiTestHostFixture fixture, ITestOutputHelper output) : IClassFixture<ApiTestHostFixture>
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

    private static object Stage(string id, string name, params string[] inputs) =>
        new { stage_id = id, name, role = name, instructions = $"Do the {name.ToLowerInvariant()}.", inputs };

    private static async Task<(HttpClient Api, string Id)> CreateAsync(ApiTestHost host, string org)
    {
        var api = host.ClientFor(await host.CreateOrganizationAsync(org));
        var created = await Json(await api.PostAsJsonAsync("/api/workspaces", new
        {
            name = "Canvas",
            goal = "Write reports.",
            pipeline = new { stages = new[] { Stage("research", "Research"), Stage("write", "Write", "research") } }
        }));
        return (api, created.GetProperty("workspace_id").GetString()!);
    }

    private static List<string> Inputs(JsonElement pipeline, string stageId) =>
        pipeline.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage_id").GetString() == stageId)
            .GetProperty("inputs").EnumerateArray().Select(i => i.GetString()!).ToList();

    [Fact]
    public async Task Stages_are_moved_connected_and_dropped_by_hand()
    {
        if (Skip()) return;
        var (api, id) = await CreateAsync(Host, "CanvasEdits");
        using var client = api;

        // Moving stages saves their places on the current version.
        var moved = await Json(await api.PutAsJsonAsync($"/api/workspaces/{id}/pipeline/layout", new
        {
            layout = new Dictionary<string, object> { ["research"] = new { x = 10.4, y = 20 }, ["write"] = new { x = 400, y = 20 }, ["ghost"] = new { x = 1, y = 1 } }
        }));
        Assert.Equal(1, moved.GetProperty("version").GetInt32());
        Assert.Equal(10, moved.GetProperty("layout").GetProperty("research").GetProperty("x").GetDouble());
        Assert.False(moved.GetProperty("layout").TryGetProperty("ghost", out _));

        // A new agent dropped on the canvas, taking Research's result, sits where it was dropped.
        var dropped = await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits", new
        {
            base_version = 1,
            ops = new object[] { new { op = "add_stage", stage = new { name = "Fact check", instructions = "Check the facts.", inputs = new[] { "research" } }, position = new { x = 200, y = 220 } } }
        }));
        var pipeline = dropped.GetProperty("pipeline");
        Assert.Equal(2, pipeline.GetProperty("version").GetInt32());
        Assert.Equal(["research"], Inputs(pipeline, "fact-check"));
        Assert.Equal(220, pipeline.GetProperty("layout").GetProperty("fact-check").GetProperty("y").GetDouble());
        Assert.Equal(400, pipeline.GetProperty("layout").GetProperty("write").GetProperty("x").GetDouble());

        // A connection drawn from Fact check to Write, then the old one removed.
        await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits", new { base_version = 2, ops = new object[] { new { op = "connect", from = "fact-check", to = "write" } } }));
        pipeline = (await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits",
            new { base_version = 3, ops = new object[] { new { op = "disconnect", from = "research", to = "write" } } }))).GetProperty("pipeline");
        Assert.Equal(["fact-check"], Inputs(pipeline, "write"));

        // A connection that makes a loop is refused.
        var loop = await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits", new { base_version = 4, ops = new object[] { new { op = "connect", from = "write", to = "research" } } });
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);

        // Removing a stage drops its place; "Tidy up" clears the rest.
        pipeline = (await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits",
            new { base_version = 4, ops = new object[] { new { op = "remove_stage", stage_id = "fact-check" } } }))).GetProperty("pipeline");
        Assert.False(pipeline.GetProperty("layout").TryGetProperty("fact-check", out _));
        var tidy = await Json(await api.PutAsJsonAsync($"/api/workspaces/{id}/pipeline/layout", new { layout = new { } }));
        Assert.Empty(tidy.GetProperty("layout").EnumerateObject());
        Assert.Equal(5, tidy.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_stage_model_must_exist_and_mentions_tell_the_editor_which_model_and_stage()
    {
        if (Skip()) return;
        var (api, id) = await CreateAsync(Host, "CanvasModels");
        using var client = api;

        var unknown = await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/edits", new
        {
            base_version = 1,
            ops = new object[] { new { op = "update_stage", stage_id = "write", stage = new { model_profile_id = "no-such-model" } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("no-such-model", await unknown.Content.ReadAsStringAsync());

        // "@default-model" and "@write" are explained to the editor, which sets that stage's model.
        var proposal = await Json(await api.PostAsJsonAsync($"/api/workspaces/{id}/pipeline/propose", new { request = "Use @default-model for @write" }));
        Assert.True(proposal.GetProperty("valid").GetBoolean(), proposal.ToString());
        var write = proposal.GetProperty("preview").GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage_id").GetString() == "write");
        Assert.Equal("server", write.GetProperty("model_profile_id").GetString());
    }
}
