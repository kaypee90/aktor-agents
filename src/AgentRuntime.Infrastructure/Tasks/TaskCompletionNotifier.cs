using System.Collections.Concurrent;

namespace AgentRuntime.Infrastructure.Tasks;

/// <summary>
/// Wakes in-process waiters (MCP and A2A callers blocking on a task, long-poll requests, the
/// webhook dispatcher) as soon as a task's final result has been saved. It is only a shortcut:
/// the saved task row is the truth, so a waiter always re-reads it, and the webhook dispatcher also
/// sweeps the table for anything a restart made it miss.
/// </summary>
public sealed class TaskCompletionNotifier
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();

    /// <summary>Raised after a task's result is saved (on the persistence thread: keep handlers short).</summary>
    public event Action<string>? Completed;

    public void Signal(string taskId)
    {
        if (_waiters.TryRemove(taskId, out var tcs)) tcs.TrySetResult();
        Completed?.Invoke(taskId);
    }

    /// <summary>Returns a task that finishes when <paramref name="taskId"/> is next signalled.
    /// Register before checking the saved row, so a completion between the two isn't missed.</summary>
    public Task WhenSignalled(string taskId) =>
        _waiters.GetOrAdd(taskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
}
