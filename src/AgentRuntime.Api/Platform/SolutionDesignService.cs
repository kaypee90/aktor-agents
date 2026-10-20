using System.Text.Json.Nodes;
using AgentRuntime.Integrations;
using AgentRuntime.LLM;
using AgentRuntime.Solutions;
using AgentRuntime.Tenancy;

namespace AgentRuntime.Api.Platform;

/// <summary>A conversation turn's answer: what the designer said, and the draft its changes would
/// make, already checked like a package, for a person to apply in the builder (or not).</summary>
public sealed record SolutionDesignProposal
{
    public string Reply { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public List<SolutionEditOp> Ops { get; init; } = [];
    /// <summary>What applying it changes, one line each.</summary>
    public List<string> Changes { get; init; } = [];
    /// <summary>Why it can't be applied (the model failed, or its edits don't fit the draft).</summary>
    public List<string> Errors { get; init; } = [];
    /// <summary>The draft after the changes (null when nothing changes or it can't be applied).</summary>
    public JsonObject? Manifest { get; init; }
    public List<DraftSkill>? Skills { get; init; }
    /// <summary>What validating the changed draft found: it can still be applied and fixed in the forms.</summary>
    public List<string> Problems { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool CanApply => Errors.Count == 0 && Changes.Count > 0 && Manifest is not null;
}

/// <summary>
/// Designs solutions from plain language (docs/solutions.md#designing-in-a-conversation): one model
/// call proposes edits to the builder's draft, <see cref="SolutionDraftEditor"/> applies them, and
/// the result is validated exactly as a package would be. Nothing is saved or deployed: the builder
/// shows the proposal and a person applies it. Paid by the organization like any model call.
/// </summary>
public sealed partial class SolutionDesignService(
    ILLMProvider llm,
    ILlmSettingsResolver llmSettings,
    SolutionService solutions,
    PluginCatalog plugins,
    IGlobalConnectorStore globalConnectors,
    IGrainFactory grains,
    ILogger<SolutionDesignService> logger)
{
    private const int MaxOutputTokens = 8000;
    public const int MaxRequestChars = 4000;

    public async Task<SolutionDesignProposal> ProposeAsync(string tenantId, JsonObject draft, IReadOnlyList<DraftSkill> skills,
        IReadOnlyList<DesignTurn> history, string request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request)) return new SolutionDesignProposal { Errors = ["Describe what you want the solution to do."] };
        if (request.Length > MaxRequestChars) return new SolutionDesignProposal { Errors = [$"Keep a message under {MaxRequestChars:N0} characters."] };

        var tenant = grains.GetGrain<ITenantGrain>(TenantIds.Normalize(tenantId));
        var quota = await tenant.CheckQuota();
        if (!quota.Allowed) return new SolutionDesignProposal { Errors = [quota.Reason ?? "The organization's plan limit is reached."] };

        var context = new SolutionDesignContext
        {
            Plugins = plugins.All
                .Where(p => p.Manifest.Id != Infrastructure.Solutions.RecordsPlugin.PluginId)
                .Select(p => (p.Manifest.Id, p.Manifest.Name, string.Join(", ", p.Manifest.Settings.Where(f => !f.Secret).Select(f => f.Key))))
                .ToList(),
            GlobalConnectors = (await globalConnectors.ListAsync(tenantId, ct)).Select(g => (g.Name, g.PluginId)).ToList()
        };
        var prices = await llmSettings.ResolveAsync(tenantId, null, ct);
        var llmRequest = SolutionDesignPrompt.BuildRequest(draft, skills, history, request, context, prices.Model, MaxOutputTokens) with
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
            logger.LogWarning(ex, "Solution designer call failed");
            return new SolutionDesignProposal { Errors = [$"The model couldn't be reached: {ex.Message}"] };
        }

        await tenant.RecordUsage(new UsageDelta { Tokens = response.InputTokens + response.OutputTokens, CostUsd = prices.CostOf(response), LlmCalls = 1 });

        var (proposal, error) = SolutionDesignPrompt.Parse(response);
        if (proposal is null) return new SolutionDesignProposal { Errors = [error!] };
        if (proposal.Ops.Count == 0) return new SolutionDesignProposal { Reply = proposal.Reply, Summary = proposal.Summary };

        var applied = SolutionDraftEditor.Apply(draft, skills, proposal.Ops);
        var result = new SolutionDesignProposal
        {
            Reply = proposal.Reply,
            Summary = proposal.Summary,
            Ops = proposal.Ops,
            Changes = applied.Changes,
            Errors = applied.Errors
        };
        if (applied.Errors.Count > 0) return result;

        var check = solutions.Check(ToBundle(applied.Draft, applied.Skills));
        return result with
        {
            Manifest = applied.Draft,
            Skills = applied.Skills,
            Problems = check.Validation.Errors,
            Warnings = check.Validation.Warnings
        };
    }

    /// <summary>The draft as a package, the way the builder writes it: the manifest as JSON (which the
    /// server reads like YAML), and each skill as skills/&lt;name&gt;/SKILL.md.</summary>
    public static SolutionBundle ToBundle(JsonObject draft, IReadOnlyList<DraftSkill> skills)
    {
        var manifest = (JsonObject)draft.DeepClone();
        var files = new Dictionary<string, string>();
        var refs = new JsonArray();
        foreach (var skill in skills.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            var slug = SlugRegex().Replace(skill.Name.ToLowerInvariant(), "-").Trim('-');
            var path = $"skills/{(slug.Length == 0 ? "skill" : slug[..Math.Min(slug.Length, 40)])}/SKILL.md";
            files[path] = $"---\nname: {Quote(skill.Name)}\ndescription: {Quote(skill.Description)}\n---\n\n{skill.Instructions.Trim()}\n";
            refs.Add(new JsonObject { ["path"] = path, ["workspace"] = string.IsNullOrWhiteSpace(skill.Workspace) ? null : skill.Workspace });
        }

        manifest["skills"] = refs;
        return new SolutionBundle { Manifest = manifest.ToJsonString(), Files = files };
    }

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex SlugRegex();

    private static string Quote(string value) =>
        value.IndexOfAny([':', '#', '\'', '"', '\n']) >= 0 || value.Trim() != value ? System.Text.Json.JsonSerializer.Serialize(value) : value;
}
