using AgentRuntime.Workspaces;
using Xunit;

namespace AgentRuntime.Tests;

public class WorkPlannerTests
{
    private static PlannedPart Part(string title, string size, bool depends = false) => new(title, size, depends);

    [Fact]
    public void SeveralSubstantialIndependentParts_Split_OneWorkerEach()
    {
        var plan = WorkPlanner.Recommend(
        [
            Part("Research Togo's government", "medium"),
            Part("Research Nigeria's government", "large"),
            Part("Research Ivory Coast's government", "medium"),
            Part("Write the comparison", "small", depends: true)
        ], maxWorkers: 3);

        Assert.Equal("split", plan.Approach);
        Assert.Equal(3, plan.Workers);
        Assert.Equal(["Write the comparison"], plan.YourParts);
        Assert.Contains("combine their results", plan.Guidance);
    }

    [Fact]
    public void MorePartsThanWorkers_AreGrouped()
    {
        var plan = WorkPlanner.Recommend([.. Enumerable.Range(1, 5).Select(i => Part($"Country {i}", "medium"))], maxWorkers: 3);

        Assert.Equal(3, plan.Workers);
        Assert.Contains("group them", plan.Guidance);
    }

    [Fact]
    public void SmallParts_AreDoneBySelf()
    {
        var plan = WorkPlanner.Recommend([Part("Suggest names", "small"), Part("Pick the best", "small")], maxWorkers: 3);

        Assert.Equal("self", plan.Approach);
        Assert.Equal(0, plan.Workers);
    }

    [Fact]
    public void ASequenceOfSteps_NeverSplitsIntoParallelWorkers_OnlyItsLargeFirstStepIsDelegated()
    {
        var plan = WorkPlanner.Recommend(
        [
            Part("Research the market", "large"),
            Part("Draft the plan from the research", "large", depends: true),
            Part("Email it", "small", depends: true)
        ], maxWorkers: 3);

        Assert.Equal("delegate", plan.Approach);
        Assert.Equal(1, plan.Workers);
        Assert.Equal(["Research the market"], plan.WorkerParts);
    }

    [Fact]
    public void OneMediumPart_IsDoneBySelf() =>
        Assert.Equal("self", WorkPlanner.Recommend([Part("Write a short report", "medium")], maxWorkers: 3).Approach);

    [Fact]
    public void OneLargePart_IsDelegated_SoTheCoordinatorStaysResponsive()
    {
        var plan = WorkPlanner.Recommend([Part("Write a 20-page market study", "large")], maxWorkers: 3);

        Assert.Equal("delegate", plan.Approach);
        Assert.Equal(1, plan.Workers);
    }

    [Fact]
    public void NoWorkersAllowed_MeansSelf() =>
        Assert.Equal(0, WorkPlanner.Recommend([Part("A", "large"), Part("B", "large")], maxWorkers: 0).Workers);
}
