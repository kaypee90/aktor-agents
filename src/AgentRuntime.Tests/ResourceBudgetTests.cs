using AgentRuntime.Contracts;
using Xunit;

namespace AgentRuntime.Tests;

public class ResourceBudgetTests
{
    [Fact]
    public void DeriveChildBudget_DefaultsToAnEqualShareOfRemaining()
    {
        // 5 child slots + 1 share the parent keeps = 6 shares of the 600 tokens remaining.
        var parentBudget = new ResourceBudget { MaxTokens = 1000, MaxToolCalls = 60, MaxCostUsd = 6m, MaxChildren = 5 };
        var parentUsage = new ResourceUsage { TokensUsed = 400 };

        var child = parentBudget.DeriveChildBudget(parentUsage, requested: null);

        Assert.Equal(100, child.MaxTokens);
        Assert.Equal(10, child.MaxToolCalls);
        Assert.Equal(1m, child.MaxCostUsd);
    }

    [Fact]
    public void DeriveChildBudget_ReservedBudgetIsNotAvailableToLaterChildren()
    {
        var parentBudget = new ResourceBudget { MaxTokens = 1000, MaxChildren = 5 };
        var parentUsage = new ResourceUsage { ReservedTokens = 1000, ChildrenSpawned = 2 };

        var child = parentBudget.DeriveChildBudget(parentUsage, requested: null);

        Assert.Equal(0, child.MaxTokens);
        Assert.True(child.IsEmpty);
    }

    [Fact]
    public void DeriveChildBudget_SiblingsTogetherNeverExceedTheParentBudget()
    {
        var parentBudget = new ResourceBudget { MaxTokens = 10_000, MaxToolCalls = 100, MaxCostUsd = 2m, MaxChildren = 5 };
        var usage = new ResourceUsage();
        var grantedTokens = 0;

        for (var i = 0; i < 5; i++)
        {
            var child = parentBudget.DeriveChildBudget(usage, requested: null);
            grantedTokens += child.MaxTokens;
            usage = usage with
            {
                ChildrenSpawned = usage.ChildrenSpawned + 1,
                ReservedTokens = usage.ReservedTokens + child.MaxTokens,
                ReservedToolCalls = usage.ReservedToolCalls + child.MaxToolCalls,
                ReservedCostUsd = usage.ReservedCostUsd + child.MaxCostUsd
            };
        }

        Assert.True(grantedTokens <= parentBudget.MaxTokens);
        Assert.True(parentBudget.RemainingTokens(usage) > 0, "the parent should keep a share for its own work");
    }

    [Fact]
    public void DeriveChildBudget_ClampsRequestedAmountToRemaining()
    {
        var parentBudget = new ResourceBudget { MaxTokens = 1000 };
        var parentUsage = new ResourceUsage { TokensUsed = 900 };

        // Child asks for way more than the parent has left.
        var requested = new ResourceBudget { MaxTokens = 5000 };

        var child = parentBudget.DeriveChildBudget(parentUsage, requested);

        Assert.Equal(100, child.MaxTokens);
    }

    [Fact]
    public void DeriveChildBudget_WhenParentExhausted_GrantsZero()
    {
        var parentBudget = new ResourceBudget { MaxTokens = 1000, MaxCostUsd = 2m };
        var parentUsage = new ResourceUsage { TokensUsed = 1000, CostUsd = 2m };

        var child = parentBudget.DeriveChildBudget(parentUsage, requested: null);

        Assert.Equal(0, child.MaxTokens);
        Assert.Equal(0m, child.MaxCostUsd);
    }

    [Fact]
    public void DeriveChildBudget_MaxChildrenNeverExceedsParentsConfiguredMax()
    {
        var parentBudget = new ResourceBudget { MaxChildren = 3 };
        var requested = new ResourceBudget { MaxChildren = 10 };

        var child = parentBudget.DeriveChildBudget(new ResourceUsage(), requested);

        Assert.Equal(3, child.MaxChildren);
    }

    [Fact]
    public void DeriveChildBudget_DurationIsNotSplit_ButNeverOutlivesParent()
    {
        var parentBudget = new ResourceBudget { MaxDurationSeconds = 900 };
        var parentUsage = new ResourceUsage { ElapsedSeconds = 300 };

        var child = parentBudget.DeriveChildBudget(parentUsage, requested: null);

        Assert.Equal(600, child.MaxDurationSeconds);
    }
}
