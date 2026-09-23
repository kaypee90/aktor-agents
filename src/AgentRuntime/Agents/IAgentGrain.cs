using AgentRuntime.Contracts;
using AgentRuntime.Messaging;
using Orleans.Concurrency;

namespace AgentRuntime.Agents;

/// <summary>
/// Actor contract for a single agent (CLAUDE.md section 7). The grain owns its state exclusively;
/// every other agent — and the API — interacts with it only through these messages.
/// </summary>
public interface IAgentGrain : IGrainWithStringKey
{
    Task Initialize(AgentInitializationRequest request);

    /// <summary>
    /// Kicks off the agent's reasoning loop, which can run for many LLM turns. One-way so callers
    /// don't hold a request open for the whole loop — a normal call would hit Orleans' response
    /// timeout (30s) on any agent that thinks longer than that and log a spurious failure, even
    /// though the agent kept running fine. Failures surface through the agent's own status and
    /// AgentFailed events instead.
    /// </summary>
    [OneWay]
    Task Start();

    /// <summary>
    /// Always interleaves: it only enqueues the message into the agent's in-memory inbox (no state
    /// write) and schedules a one-way wake. Without this, a sender would block for the recipient's
    /// entire reasoning turn, and two agents messaging each other mid-turn would deadlock until the
    /// Orleans response timeout.
    /// </summary>
    [AlwaysInterleave]
    Task<AgentMessageAck> SendMessage(AgentMessage message);

    /// <summary>Always interleaves for the same reason as <see cref="SendMessage"/>.</summary>
    [AlwaysInterleave]
    Task HandleEvent(EnvironmentEvent environmentEvent);

    /// <summary>
    /// Always interleaves so an operator can pause an agent that is mid-turn: it records the
    /// request, and the reasoning loop (or a queued wake) applies it at the next safe point.
    /// </summary>
    [AlwaysInterleave]
    Task Pause();

    Task Resume();

    /// <summary>Always interleaves so an operator can stop a mid-turn agent; applied at the next
    /// safe point in the reasoning loop (or immediately via a queued wake when idle).</summary>
    [AlwaysInterleave]
    Task Stop();

    /// <summary>
    /// Applies pending operator requests (pause/stop), drains the inbox into the transcript, and
    /// triggers the reasoning loop if the agent is Idle/Waiting, not paused, and has new input
    /// (or was explicitly resumed); otherwise is a no-op.
    /// Marked one-way (CLAUDE.md section 48 — event-driven wake) so a caller enqueuing this never
    /// blocks on the resulting reasoning turn: without this, agent A sending a message to agent B
    /// and awaiting B's full turn — which might reply back to A — would deadlock against Orleans'
    /// non-reentrant grain turns the moment two agents reply to each other.
    /// </summary>
    [OneWay]
    Task WakeAndThink();

    /// <summary>
    /// Read-only, so marked to always interleave (CLAUDE.md section 48): without this, a status
    /// check for an agent that is itself mid-turn (e.g. the orchestrator inspecting the very agent
    /// that is currently calling spawn_agent) would queue behind that turn and deadlock, since the
    /// turn is often the one awaiting this call's result.
    /// </summary>
    [AlwaysInterleave]
    Task<AgentSnapshot> GetSnapshot();

    [AlwaysInterleave]
    Task<AgentStatus> GetStatus();
}
