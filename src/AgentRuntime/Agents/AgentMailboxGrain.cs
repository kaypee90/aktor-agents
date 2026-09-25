using AgentRuntime.Durability;
using AgentRuntime.Messaging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace AgentRuntime.Agents;

public enum MailKind
{
    Message,
    Event,
    Control
}

public enum ControlKind
{
    Pause,
    Resume,
    /// <summary>Clears a pause without forcing a turn; the next message or event wakes the agent.</summary>
    Unpause,
    Stop
}

[GenerateSerializer]
public sealed record MailItem
{
    /// <summary>Deduplication key: the message id, event id, or a control command id.</summary>
    [Id(0)] public required string Id { get; init; }
    [Id(1)] public long Seq { get; init; }
    [Id(2)] public required MailKind Kind { get; init; }
    [Id(3)] public AgentMessage? Message { get; init; }
    [Id(4)] public EnvironmentEvent? Event { get; init; }
    [Id(5)] public ControlKind? Control { get; init; }
    /// <summary>Stop reason (retire), recorded on the termination event.</summary>
    [Id(6)] public string? Reason { get; init; }
    [Id(7)] public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed class MailboxState
{
    [Id(0)] public List<MailItem> Items { get; set; } = [];
    [Id(1)] public long NextSeq { get; set; } = 1;
    /// <summary>Recently seen ids (bounded), so a redelivered item is dropped even after it was
    /// acknowledged and removed.</summary>
    [Id(2)] public List<string> RecentIds { get; set; } = [];
}

/// <summary>
/// A durable mailbox for one agent (same key as the agent). Everything addressed to an agent —
/// messages, environment events, and operator control (pause/stop) — is written here before the
/// sender gets an acknowledgement, so a crash can never lose it. The agent reads items at safe
/// points in its turn and acknowledges them only after its own state (which records the last
/// sequence it consumed) is saved, so nothing is processed twice either.
/// </summary>
public interface IAgentMailboxGrain : IGrainWithStringKey
{
    /// <summary>Stores the item (dropping duplicates by id) and wakes the agent.</summary>
    Task Enqueue(MailItem item);

    /// <summary>Items with a sequence number above <paramref name="afterSeq"/>, oldest first.</summary>
    [AlwaysInterleave]
    Task<IReadOnlyList<MailItem>> Peek(long afterSeq);

    /// <summary>Removes items up to and including <paramref name="throughSeq"/>. One-way: the
    /// agent's saved sequence number already prevents reprocessing, so it never needs to wait.</summary>
    [OneWay]
    Task Acknowledge(long throughSeq);
}

public sealed class AgentMailboxGrain(
    [PersistentState("mailbox", "Default")] IPersistentState<MailboxState> state,
    IOptions<DurabilityOptions> durability) : Grain, IAgentMailboxGrain, IRemindable
{
    private const string DeliveryReminder = "mailbox-delivery";
    private const int RecentIdLimit = 500;

    private IAgentGrain Agent => GrainFactory.GetGrain<IAgentGrain>(this.GetPrimaryKeyString());

    public async Task Enqueue(MailItem item)
    {
        var s = state.State;
        if (s.RecentIds.Contains(item.Id) || s.Items.Any(i => i.Id == item.Id))
        {
            // Already have it (a replayed send). Still nudge the agent in case the first wake was lost.
            await Agent.WakeAndThink();
            return;
        }

        var wasEmpty = s.Items.Count == 0;
        s.Items.Add(item with { Seq = s.NextSeq++ });
        s.RecentIds.Add(item.Id);
        if (s.RecentIds.Count > RecentIdLimit) s.RecentIds.RemoveRange(0, s.RecentIds.Count - RecentIdLimit);
        await state.WriteStateAsync();

        // Redelivery safety net: if this process dies before the agent drains the item, the
        // reminder re-activates this mailbox on a surviving/restarted silo and wakes the agent.
        if (wasEmpty)
        {
            await this.RegisterOrUpdateReminder(DeliveryReminder, durability.Value.RecoveryReminderPeriod, durability.Value.RecoveryReminderPeriod);
        }

        await Agent.WakeAndThink();
    }

    public Task<IReadOnlyList<MailItem>> Peek(long afterSeq) =>
        Task.FromResult<IReadOnlyList<MailItem>>(state.State.Items.Where(i => i.Seq > afterSeq).ToList());

    public async Task Acknowledge(long throughSeq)
    {
        var s = state.State;
        var removed = s.Items.RemoveAll(i => i.Seq <= throughSeq);
        if (removed == 0) return;

        await state.WriteStateAsync();
        if (s.Items.Count == 0)
        {
            await UnregisterDeliveryReminderAsync();
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != DeliveryReminder) return;

        if (state.State.Items.Count == 0)
        {
            await UnregisterDeliveryReminderAsync();
            return;
        }

        await Agent.WakeAndThink();
    }

    private async Task UnregisterDeliveryReminderAsync()
    {
        var reminder = await this.GetReminder(DeliveryReminder);
        if (reminder is not null) await this.UnregisterReminder(reminder);
    }
}
