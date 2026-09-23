using AgentRuntime.Contracts;
using Xunit;

namespace AgentRuntime.Tests;

public class AgentStateTests
{
    [Fact]
    public void NewAgentState_StartsAsCreated()
    {
        var state = new AgentState();
        Assert.Equal(AgentStatus.Created, state.Status);
    }

    [Theory]
    [InlineData(AgentStatus.Created, AgentStatus.Initializing, true)]
    [InlineData(AgentStatus.Created, AgentStatus.Completed, false)]
    [InlineData(AgentStatus.Idle, AgentStatus.Thinking, true)]
    [InlineData(AgentStatus.Thinking, AgentStatus.Executing, true)]
    [InlineData(AgentStatus.Executing, AgentStatus.Completed, true)]
    [InlineData(AgentStatus.Completed, AgentStatus.Thinking, false)]
    [InlineData(AgentStatus.Terminated, AgentStatus.Idle, false)]
    public void CanTransitionTo_FollowsExplicitStateMachine(AgentStatus from, AgentStatus to, bool expected)
    {
        var state = new AgentState();
        // Drive to the `from` state via ForceStatus so we test CanTransitionTo in isolation.
        state.ForceStatus(from);

        Assert.Equal(expected, state.CanTransitionTo(to));
    }

    [Fact]
    public void TransitionTo_InvalidTransition_Throws()
    {
        var state = new AgentState();
        state.ForceStatus(AgentStatus.Completed);

        Assert.Throws<InvalidOperationException>(() => state.TransitionTo(AgentStatus.Thinking));
    }

    [Fact]
    public void TransitionTo_ValidTransition_UpdatesStatus()
    {
        var state = new AgentState();
        state.TransitionTo(AgentStatus.Initializing);
        state.TransitionTo(AgentStatus.Idle);

        Assert.Equal(AgentStatus.Idle, state.Status);
    }

    [Fact]
    public void ForceStatus_BypassesStateMachine()
    {
        var state = new AgentState();
        state.ForceStatus(AgentStatus.Terminated);

        Assert.Equal(AgentStatus.Terminated, state.Status);
    }
}
