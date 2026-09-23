using Orleans.Runtime;

namespace AgentRuntime.Tests;

/// <summary>In-memory stand-in for Orleans' <see cref="IPersistentState{TState}"/>, letting grain
/// classes that only depend on constructor-injected persisted state be unit tested without an
/// Orleans TestCluster.</summary>
public sealed class FakePersistentState<TState> : IPersistentState<TState> where TState : new()
{
    public TState State { get; set; } = new();
    public string Etag => "fake-etag";
    public bool RecordExists { get; private set; }

    public Task ClearStateAsync()
    {
        State = new TState();
        RecordExists = false;
        return Task.CompletedTask;
    }

    public Task WriteStateAsync()
    {
        RecordExists = true;
        return Task.CompletedTask;
    }

    public Task ReadStateAsync() => Task.CompletedTask;
}
