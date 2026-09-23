using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AgentRuntime.Events;

/// <summary>
/// Broadcast-style in-process event bus. Every subscriber (SSE clients, the Postgres event
/// writer, in-process test observers) gets its own channel so slow consumers can't block others.
/// </summary>
public sealed class InMemoryEventBus : IEventPublisher, IEventStream
{
    private readonly ConcurrentDictionary<Guid, Channel<RuntimeEvent>> _subscribers = new();

    public ValueTask PublishAsync(RuntimeEvent evt, CancellationToken cancellationToken = default)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RuntimeEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<RuntimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[id] = channel;
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
