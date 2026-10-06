using System.Text.Json;
using AgentRuntime.Pipelines;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Tests;

/// <summary>Every template is something a person can start from as is: its pipeline can run,
/// its sample input or payload is usable, and its triggers are valid.</summary>
public class WorkspaceTemplateTests
{
    public static TheoryData<string> Ids => [.. WorkspaceTemplates.All.Select(t => t.Id)];

    [Fact]
    public void Ids_are_unique_and_every_template_can_be_tried()
    {
        Assert.Equal(WorkspaceTemplates.All.Count, WorkspaceTemplates.All.Select(t => t.Id).Distinct().Count());
        Assert.All(WorkspaceTemplates.All, t => Assert.True(t.SampleInput is not null || t.Webhooks.Count > 0, $"{t.Id} has nothing to try it with"));
        Assert.True(WorkspaceTemplates.All.Count >= 9);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void The_pipeline_is_valid_and_the_triggers_parse(string id)
    {
        var template = WorkspaceTemplates.Get(id)!;
        Assert.Empty(PipelineValidator.Validate(template.Pipeline, new PipelineOptions()));
        Assert.Single(template.Pipeline.Outputs());
        Assert.False(string.IsNullOrWhiteSpace(template.Category));
        foreach (var hook in template.Webhooks) JsonDocument.Parse(hook.SamplePayload).Dispose();
        foreach (var schedule in template.Schedules) Assert.NotNull(CronSchedule.Parse(schedule.Cron).NextAfter(DateTimeOffset.UtcNow));
    }
}
