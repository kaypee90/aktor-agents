using AgentRuntime.Contracts;
using AgentRuntime.Resources;
using Xunit;

namespace AgentRuntime.Tests;

public class BudgetGuardTests
{
    private static readonly ResourceBudget Budget = new()
    {
        MaxTokens = 100_000,
        MaxToolCalls = 100,
        MaxCostUsd = 10m,
        MaxDurationSeconds = 1000
    };

    private static BudgetCheck Check(ResourceUsage usage, double elapsed = 0, int nextCall = 5_000, decimal nextCost = 0.01m) =>
        BudgetGuard.Evaluate(Budget, usage, elapsed, nextCall, nextCost, wrapUpFraction: 0.75);

    [Fact]
    public void PlentyLeft_Continues() =>
        Assert.Equal(BudgetStep.Continue, Check(new ResourceUsage { TokensUsed = 10_000 }).Step);

    [Theory]
    [InlineData(76_000, 0, 0, 0)]   // tokens
    [InlineData(0, 76, 0, 0)]       // tool calls
    [InlineData(0, 0, 7.6, 0)]      // cost
    [InlineData(0, 0, 0, 760)]      // time
    public void PastTheThresholdOnAnyDimension_WrapsUp(int tokens, int toolCalls, double cost, double elapsed)
    {
        var check = Check(new ResourceUsage { TokensUsed = tokens, ToolCallsUsed = toolCalls, CostUsd = (decimal)cost }, elapsed);
        Assert.Equal(BudgetStep.WrapUp, check.Step);
    }

    [Fact]
    public void AFewCallsFromTheEnd_WrapsUp_EvenBelowTheThreshold()
    {
        // 50% used, but each call costs 15k: only three calls' worth left.
        var check = Check(new ResourceUsage { TokensUsed = 55_000 }, nextCall: 15_000);
        Assert.Equal(BudgetStep.WrapUp, check.Step);
        Assert.Equal("token budget", check.Reason);
    }

    [Fact]
    public void RoomForExactlyOneMoreCall_IsTheFinalStep()
    {
        var check = Check(new ResourceUsage { TokensUsed = 92_000 }, nextCall: 5_000);
        Assert.Equal(BudgetStep.FinalStep, check.Step);
        Assert.Equal("token budget", check.Reason);
    }

    [Fact]
    public void NoRoomForEvenOneCall_Stops() =>
        Assert.Equal(BudgetStep.Stop, Check(new ResourceUsage { TokensUsed = 97_000 }, nextCall: 5_000).Step);

    [Fact]
    public void BudgetReservedForChildren_CountsAsSpent() =>
        Assert.Equal(BudgetStep.FinalStep, Check(new ResourceUsage { TokensUsed = 2_000, ReservedTokens = 90_000 }).Step);

    [Fact]
    public void OutOfCostForTwoCalls_IsTheFinalStep() =>
        Assert.Equal(BudgetStep.FinalStep, Check(new ResourceUsage { CostUsd = 9.99m }, nextCost: 0.006m).Step);

    [Fact]
    public void OutOfToolCalls_IsTheFinalStep_SoTheAgentCanStillReport()
    {
        var check = Check(new ResourceUsage { ToolCallsUsed = 100 });
        Assert.Equal(BudgetStep.FinalStep, check.Step);
        Assert.Equal("tool-call budget", check.Reason);
    }

    [Fact]
    public void OutOfTime_IsTheFinalStep()
    {
        var check = Check(new ResourceUsage(), elapsed: 1001);
        Assert.Equal(BudgetStep.FinalStep, check.Step);
        Assert.Equal("time budget", check.Reason);
    }
}
