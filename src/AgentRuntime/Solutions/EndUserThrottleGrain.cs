namespace AgentRuntime.Solutions;

/// <summary>
/// How often one end user may act (docs/solutions.md#the-assistant): one activation per end user
/// in the whole cluster, so the limit holds however many servers there are. Its window is kept in
/// memory; a restart forgets it, which can only make a limit more lenient for a minute.
/// </summary>
public interface IEndUserThrottleGrain : IGrainWithStringKey
{
    /// <summary>Takes one of <paramref name="limit"/> slots in the last <paramref name="window"/>;
    /// false when they're all used.</summary>
    Task<bool> TryAcquire(int limit, TimeSpan window);
}

public sealed class EndUserThrottleGrain(TimeProvider clock) : Grain, IEndUserThrottleGrain
{
    private readonly Queue<DateTimeOffset> _recent = new();

    public Task<bool> TryAcquire(int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        while (_recent.Count > 0 && now - _recent.Peek() > window) _recent.Dequeue();
        if (_recent.Count >= limit) return Task.FromResult(false);
        _recent.Enqueue(now);
        return Task.FromResult(true);
    }
}
