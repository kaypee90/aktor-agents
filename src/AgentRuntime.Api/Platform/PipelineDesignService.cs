using AgentRuntime.LLM;
using AgentRuntime.Pipelines;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Platform;

/// <summary>A proposed change to a pipeline: what the editor understood, the edits, and the
/// pipeline they would produce, for a person to look at before applying it.</summary>
public sealed record PipelineProposal
{
    /// <summary>The version the edits apply to; applying them to any other is refused.</summary>
    public int BaseVersion { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<PipelineEditOp> Ops { get; init; } = [];
    public List<string> Changes { get; init; } = [];
    /// <summary>Why the proposal can't be applied (the editor failed, or its edits break the rules).</summary>
    public List<string> Errors { get; init; } = [];
    /// <summary>The pipeline after the edits, when they're valid.</summary>
    public PipelineDefinition? Preview { get; init; }
    public bool Valid => Errors.Count == 0 && Preview is not null;
}

/// <summary>
/// Configures pipelines from plain language (docs/workspaces.md): one model call proposes edits,
/// <see cref="PipelineEditor"/> applies and validates them, and nothing changes until a person
/// applies the proposal. The call is paid like any other: by the organization, and by the
/// workspace's daily budget when it's for a workspace.
/// </summary>
public sealed class PipelineDesignService(
    ILLMProvider llm,
    ILlmSettingsResolver llmSettings,
    IGrainFactory grains,
    IOptions<PipelineOptions> options,
    ILogger<PipelineDesignService> logger)
{
    private const int MaxOutputTokens = 4000;

    /// <param name="current">The pipeline now; null to design one from scratch.</param>
    public async Task<PipelineProposal> ProposeAsync(string tenantId, string? workspaceId, string purpose, PipelineDefinition? current,
        string request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request)) return new PipelineProposal { Errors = ["Describe the change you want."] };
        var tenant = grains.GetGrain<ITenantGrain>(TenantIds.Normalize(tenantId));
        var quota = await tenant.CheckQuota();
        if (!quota.Allowed) return new PipelineProposal { Errors = [quota.Reason ?? "The organization's plan limit is reached."] };

        IReadOnlyList<string> connectionTools = workspaceId is null
            ? []
            : (await grains.GetGrain<IWorkspaceGrain>(workspaceId).GetConnectionTools()).Select(t => t.Name).ToList();
        var prices = await llmSettings.ResolveAsync(tenantId, null, ct);
        var llmRequest = PipelineDesignPrompt.BuildRequest(current, request, purpose, connectionTools, options.Value, prices.Model, MaxOutputTokens) with
        {
            TenantId = tenantId,
            ModelProfileId = prices.ProfileId ?? ModelProfiles.ServerId
        };

        LlmCompletionResponse response;
        try
        {
            response = await llm.CompleteAsync(llmRequest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pipeline editor call failed");
            return new PipelineProposal { BaseVersion = current?.Version ?? 0, Errors = [$"The model couldn't be reached: {ex.Message}"] };
        }

        var tokens = response.InputTokens + response.OutputTokens;
        var cost = prices.CostOf(response);
        await tenant.RecordUsage(new UsageDelta { Tokens = tokens, CostUsd = cost, LlmCalls = 1 });
        if (workspaceId is not null) await grains.GetGrain<IWorkspaceGrain>(workspaceId).RecordUsage("pipeline-editor", tokens, cost);

        var (proposal, error) = PipelineDesignPrompt.Parse(response);
        if (proposal is null) return new PipelineProposal { BaseVersion = current?.Version ?? 0, Errors = [error!] };

        var applied = PipelineEditor.Apply(current ?? new PipelineDefinition(), proposal.Ops, options.Value);
        return new PipelineProposal
        {
            BaseVersion = current?.Version ?? 0,
            Summary = proposal.Summary,
            Ops = proposal.Ops,
            Changes = applied.Changes,
            Errors = applied.Errors,
            Preview = applied.Pipeline
        };
    }
}
