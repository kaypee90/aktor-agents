using AgentRuntime.Contracts;
using AgentRuntime.Messaging;
using Orleans.Concurrency;

namespace AgentRuntime.Pipelines;

/// <summary>
/// One execution of a workspace's pipeline: an actor that starts each stage's agent once the
/// stage's inputs have finished, hands it their results, retries failed stages, and reports the
/// run's result. It is the root of the run's agent tree (stage agents' parent), so stages report
/// to it the way any child reports to its parent: with a completion or failure message.
/// <para>Deadlock rule: the run never makes a blocking call on one of its existing stage agents
/// (only interleaved reads and control requests), and tells its workspace one-way.</para>
/// </summary>
public interface IPipelineRunGrain : IGrainWithStringKey
{
    /// <summary>Records a run that waits for a free slot, so it already has an id and a task row
    /// a caller can poll. Idempotent.</summary>
    Task<PipelineRunView> Queue(PipelineRunRequest request);

    /// <summary>Starts the run (a queued one with <paramref name="request"/>, which carries the
    /// pipeline as it is now). Idempotent: a repeated start (a trigger fired again after a crash)
    /// returns the run as it is.</summary>
    Task<PipelineRunView> Start(PipelineRunRequest request);

    /// <summary>A message to the run: its stage agents' completion and failure notices.</summary>
    Task<AgentMessageAck> Deliver(AgentMessage message);

    [AlwaysInterleave]
    Task<PipelineRunView?> GetView();

    /// <summary>The run as the root of its agent tree, for code that reads any tree's root.</summary>
    [AlwaysInterleave]
    Task<AgentSnapshot?> GetSnapshot();

    /// <summary>Pauses the run's agents and holds back stages that haven't started.</summary>
    Task Pause();

    Task Resume();

    /// <summary>Stops every agent of the run; stages not finished are skipped.</summary>
    Task Cancel(string reason);
}
