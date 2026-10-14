using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace AgentRuntime.Pipelines;

/// <summary>
/// One attempt at an action or script stage. The run hands it over one-way and carries on; the
/// step does its work off the run's turn (a script can take seconds) and reports back with
/// <see cref="IPipelineRunGrain.OnStepFinished"/>. It keeps nothing durable: if it's lost in a
/// restart, the run notices (<see cref="GetState"/> says Unknown) and decides whether the step is
/// safe to start again.
/// </summary>
public interface IPipelineStepGrain : IGrainWithStringKey
{
    [OneWay]
    Task Execute(StepLaunch launch);

    [AlwaysInterleave]
    Task<StepState> GetState();

    /// <summary>The result, once finished (for a run whose notice was lost).</summary>
    [AlwaysInterleave]
    Task<StepResult?> GetResult();

    /// <summary>Stops the step (the run was cancelled or failed).</summary>
    [AlwaysInterleave]
    Task Cancel();
}

/// <inheritdoc cref="IPipelineStepGrain"/>
public sealed class PipelineStepGrain(PipelineStepExecutor executor, ILogger<PipelineStepGrain> logger) : Grain, IPipelineStepGrain
{
    /// <summary>Upper bound on any step, on top of its own limits (a script's timeout, tool calls').</summary>
    private static readonly TimeSpan MaxStepDuration = TimeSpan.FromMinutes(15);

    private StepState _state = StepState.Unknown;
    private StepResult? _result;
    private readonly CancellationTokenSource _cancel = new();

    public async Task Execute(StepLaunch launch)
    {
        if (_state != StepState.Unknown) return;
        _state = StepState.Running;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        limit.CancelAfter(MaxStepDuration);
        StepResult result;
        try
        {
            result = await executor.ExecuteAsync(launch, limit.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Step {StepId} of run {RunId} failed", launch.StepId, launch.RunId);
            result = StepResult.Fail($"The step failed: {ex.Message}");
        }

        _result = result;
        _state = StepState.Finished;

        // Reported until the run has it; if every attempt fails, the run's check reminder asks.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await GrainFactory.GetGrain<IPipelineRunGrain>(launch.RunId).OnStepFinished(launch.Stage.StageId, launch.StepId, result);
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                logger.LogWarning(ex, "Step {StepId} couldn't report to run {RunId}; retrying", launch.StepId, launch.RunId);
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Step {StepId} couldn't report to run {RunId}", launch.StepId, launch.RunId);
            }
        }
    }

    public Task<StepState> GetState() => Task.FromResult(_state);

    public Task<StepResult?> GetResult() => Task.FromResult(_result);

    public Task Cancel()
    {
        _cancel.Cancel();
        return Task.CompletedTask;
    }
}
