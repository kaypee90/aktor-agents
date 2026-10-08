using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Integrations;
using AgentRuntime.Messaging;
using AgentRuntime.Safety;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace AgentRuntime.Workspaces;

/// <inheritdoc cref="IWorkspaceGrain"/>
public sealed partial class WorkspaceGrain(
    [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> state,
    IAgentOrchestrator orchestrator,
    IEventPublisher events,
    IWorkspaceArchive archive,
    IOptions<WorkspaceOptions> options,
    PluginCatalog plugins,
    IntegrationService integrations,
    ISecretStore secretStore,
    IOptions<IntegrationsOptions> integrationOptions,
    IOptions<Durability.DurabilityOptions> durabilityOptions,
    IAuditLog audit,
    IOptions<Pipelines.PipelineOptions> pipelineOptions,
    ILogger<WorkspaceGrain> logger) : Grain, IWorkspaceGrain, IRemindable
{
    private const string TriggerReminderPrefix = "trigger-";
    private const string OutboxReminder = "notify-outbox";

    /// <summary>Same shape as the rest of the API: snake_case with enum names, not numbers.</summary>
    private static readonly JsonSerializerOptions OwnerJson = new(ToolJson.Options)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly IntegrationsOptions _integrations = integrationOptions.Value;
    private const int ActionResultCacheSize = 2000;

    private readonly WorkspaceOptions _opts = options.Value;
    private readonly Pipelines.PipelineOptions _pipelineOpts = pipelineOptions.Value;

    private WorkspaceState S => state.State;
    private bool Exists => !string.IsNullOrEmpty(S.WorkspaceId);
    private IAgentRegistryGrain Registry => GrainFactory.GetGrain<IAgentRegistryGrain>(0);

    // ---- Lifecycle ----------------------------------------------------------

    public async Task Create(WorkspaceCreationRequest request)
    {
        if (Exists) return; // Idempotent: a retried create is a no-op.

        var s = S;
        s.WorkspaceId = this.GetPrimaryKeyString();
        s.Name = Clip(request.Name, 100, "Workspace");
        s.Goal = Clip(request.Goal, _opts.MaxMessageLength, string.Empty);
        s.OwnerId = request.OwnerId;
        s.TenantId = Tenancy.TenantIds.Normalize(request.TenantId);
        s.CreatedAt = DateTimeOffset.UtcNow;
        s.DailyTokenLimit = Math.Max(1_000, request.DailyTokenLimit ?? _opts.DefaultDailyTokenLimit);
        s.DailyCostLimitUsd = Math.Max(0.01m, request.DailyCostLimitUsd ?? _opts.DefaultDailyCostLimitUsd);
        s.TemplateId = request.TemplateId;
        if (request.SafetyPolicy is { } policy)
        {
            s.SafetyPolicy = policy with { ApprovalTimeoutHours = Math.Clamp(policy.ApprovalTimeoutHours, 1, 24 * 30), Rules = policy.Rules.Take(50).ToList() };
        }

        var pipeline = request.Pipeline is { } given && Pipelines.PipelineValidator.Validate(given, _pipelineOpts).Count == 0
            ? given
            : DefaultPipeline(s.Goal);
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Workspace created with a {pipeline.Stages.Count}-stage pipeline: {string.Join(" → ", Pipelines.PipelineValidator.TopologicalOrder(pipeline)!.Select(st => st.Name))}. " +
            $"Run it with an input, or add a trigger to run it automatically. Daily budget: {s.DailyTokenLimit:N0} tokens / ${s.DailyCostLimitUsd:F2}.");
        await CommitPipelineAsync(pipeline, request.CreatedBy ?? request.OwnerId, request.TemplateId is null ? "Created" : $"Created from the '{request.TemplateId}' template");

        await PublishAsync(RuntimeEventType.WorkspaceCreated, $"Workspace '{s.Name}' created.",
            new Dictionary<string, string> { ["name"] = s.Name });
        await ArchiveAsync();
    }

    public Task<string?> GetTenantId() => Task.FromResult(Exists ? Tenancy.TenantIds.Normalize(S.TenantId) : null);

    public async Task Pause()
    {
        if (!Exists || S.Status != WorkspaceStatus.Active) return;

        S.Status = WorkspaceStatus.Paused;
        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace paused: triggers won't fire, runs in progress are paused and new runs wait.");
        await SaveAsync();
        foreach (var runId in S.ActiveRunIds.ToList())
        {
            await GrainFactory.GetGrain<Pipelines.IPipelineRunGrain>(runId).Pause();
        }

        await AuditAsync("user", "user", "You", "workspace.paused", S.Name, "ok", $"Workspace paused");
        await ChangedAsync("paused");
    }

    public async Task Resume()
    {
        if (!Exists || S.Status != WorkspaceStatus.Paused) return;

        S.Status = WorkspaceStatus.Active;
        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace resumed.");
        await SaveAsync();
        foreach (var runId in S.ActiveRunIds.ToList())
        {
            await GrainFactory.GetGrain<Pipelines.IPipelineRunGrain>(runId).Resume();
        }

        await DrainRunQueueAsync();

        await AuditAsync("user", "user", "You", "workspace.resumed", S.Name, "ok", $"Workspace resumed");
        await ChangedAsync("resumed");
    }

    public async Task Archive()
    {
        if (!Exists || S.Status == WorkspaceStatus.Archived) return;

        S.Status = WorkspaceStatus.Archived;
        foreach (var trigger in S.Triggers.Values.Where(t => t.Kind is TriggerKind.Schedule or TriggerKind.Watch))
        {
            await UnregisterTriggerReminderAsync(trigger.TriggerId);
        }

        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace archived: runs cancelled and triggers stopped.");
        var queued = S.RunQueue.Select(q => S.Runs.FirstOrDefault(r => r.Number == q.Number)?.RunId).OfType<string>().ToList();
        S.RunQueue.Clear();
        await SaveAsync();
        foreach (var runId in S.ActiveRunIds.Concat(queued).ToList())
        {
            await GrainFactory.GetGrain<Pipelines.IPipelineRunGrain>(runId).Cancel("the workspace was archived");
        }

        await AuditAsync("user", "user", "You", "workspace.archived", S.Name, "ok", $"Workspace archived");
        await ChangedAsync("archived");
    }

    public async Task UpdateBudget(int? dailyTokenLimit, decimal? dailyCostLimitUsd)
    {
        if (!Exists) return;
        if (dailyTokenLimit is { } t) S.DailyTokenLimit = Math.Max(1_000, t);
        if (dailyCostLimitUsd is { } c) S.DailyCostLimitUsd = Math.Max(0.01m, c);
        S.BudgetNoticeDay = null;
        S.BudgetWarningDay = null;
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Daily budget set to {S.DailyTokenLimit:N0} tokens / ${S.DailyCostLimitUsd:F2}.");
        await SaveAsync();
        await ChangedAsync("budget");
    }

    // ---- Conversation -------------------------------------------------------

    public async Task<ChatEntry> PostUserMessage(string text, string? toAgentId, string? clientMessageId, string? startedBy = null)
    {
        // "approve A7" typed in the chat decides the approval instead of reaching an agent.
        var command = ApprovalCommand().Match(text.Trim());
        if (Exists && toAgentId is null && command.Success &&
            S.Approvals.Values.Any(a => a.Code.Equals(command.Groups[2].Value, StringComparison.OrdinalIgnoreCase)))
        {
            var approve = command.Groups[1].Value.ToLowerInvariant() is "approve" or "yes";
            var result = await DecideApproval(command.Groups[2].Value, approve,
                command.Groups[3].Value is { Length: > 0 } why ? why.Trim() : null, "user", "chat");
            if (!result.Success)
            {
                AppendChat(ChatAuthorKind.System, "system", "Workspace", result.Message);
                await SaveAsync();
                await ChangedAsync("approval decision refused");
            }

            return S.Conversation[^1];
        }

        return await PostUserMessageCoreAsync(text, toAgentId, clientMessageId, "You", startedBy);
    }

    /// <summary>
    /// A message in the workspace chat. Addressed to an agent of a run in progress, it reaches that
    /// agent (guidance mid-run). Otherwise it is the input of a new run of the pipeline.
    /// </summary>
    private async Task<ChatEntry> PostUserMessageCoreAsync(string text, string? toAgentId, string? clientMessageId, string authorName, string? startedBy = null)
    {
        if (!Exists) throw new InvalidOperationException("No such workspace.");
        if (S.Status == WorkspaceStatus.Archived) throw new InvalidOperationException("This workspace is archived.");

        var dedupeKey = clientMessageId is null ? null : $"user-msg:{clientMessageId}";
        if (dedupeKey is not null && S.ActionResults.ContainsKey(dedupeKey))
        {
            return S.Conversation.LastOrDefault(c => c.AuthorKind == ChatAuthorKind.User) ?? S.Conversation[^1];
        }

        var body = Clip(text, _opts.MaxMessageLength, string.Empty);
        if (body.Length == 0) throw new ArgumentException("Message text is required.");

        AgentDirectoryEntry? target = null;
        if (!string.IsNullOrWhiteSpace(toAgentId))
        {
            target = await Registry.GetAsync(toAgentId);
            if (target is null || !S.ActiveRunIds.Contains(target.RootAgentId))
            {
                throw new ArgumentException($"No agent '{toAgentId}' in a run in progress here.");
            }

            if (IsTerminal(target.Status))
            {
                throw new ArgumentException($"'{target.Role}' has finished and can't take new instructions. Start a new run instead.");
            }
        }

        var chat = AppendChat(ChatAuthorKind.User, "user", authorName, body);
        if (dedupeKey is not null) Remember(dedupeKey, WorkspaceActionResult.Ok("delivered"));
        await SaveAsync();
        await AuditAsync("user", "user", authorName, "user.command", target?.AgentId ?? "pipeline", "ok", Truncate(body, 300), key: $"{S.WorkspaceId}-chat-{chat.Seq}");
        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"User: {Truncate(body, 120)}", ChatData(chat));

        if (target is not null)
        {
            // Durable delivery: the message id is fixed by the chat entry, so a retry of this call
            // can't reach the agent twice.
            await orchestrator.SendMessageAsync(new AgentMessage
            {
                MessageId = $"{S.WorkspaceId}-chat-{chat.Seq}",
                FromAgentId = "user",
                ToAgentId = target.AgentId,
                MessageType = MessageType.TaskRequest,
                TaskId = target.RootAgentId,
                Payload = body
            });
        }
        else
        {
            var started = await StartRunCoreAsync(body, "chat", startedBy, trigger: null,
                eventId: dedupeKey is null ? null : $"{S.WorkspaceId}:{dedupeKey}");
            if (!started.Success)
            {
                AppendChat(ChatAuthorKind.System, "system", "Workspace", started.Message, "warning");
                await SaveAsync();
            }
        }

        await ArchiveAsync();
        return chat;
    }

    // ---- Triggers -----------------------------------------------------------

    public async Task<WorkspaceActionResult> AddTrigger(TriggerSpec spec, string createdBy, string idempotencyKey, bool revealSecret)
    {
        if (TryReplay(idempotencyKey, out var replayed)) return replayed;
        if (!Exists) return WorkspaceActionResult.Fail("No such workspace.");
        if (S.Status == WorkspaceStatus.Archived) return WorkspaceActionResult.Fail("This workspace is archived.");
        if (S.Triggers.Count >= _opts.MaxTriggersPerWorkspace)
        {
            return WorkspaceActionResult.Fail($"This workspace already has the maximum of {_opts.MaxTriggersPerWorkspace} triggers.");
        }

        var name = Clip(spec.Name, 80, string.Empty);
        if (name.Length == 0) return WorkspaceActionResult.Fail("A trigger needs a name.");

        var trigger = new TriggerDefinition
        {
            TriggerId = DeterministicId.FromOrNew("trg-", string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey),
            Kind = spec.Kind,
            Name = name,
            Instruction = Clip(spec.Instruction, 1000, string.Empty),
            CreatedBy = createdBy
        };

        object? preview = null;
        if (spec.Kind == TriggerKind.Watch)
        {
            var (watchError, watchPreview) = await PrepareWatchAsync(trigger, spec);
            if (watchError is not null) return WorkspaceActionResult.Fail(watchError);
            preview = watchPreview;
        }

        if (spec.Kind is TriggerKind.Schedule or TriggerKind.Watch)
        {
            if (!string.IsNullOrWhiteSpace(spec.Cron))
            {
                if (!CronSchedule.TryParse(spec.Cron, out var cron, out var error)) return WorkspaceActionResult.Fail($"Invalid cron expression: {error}");
                var next = cron!.NextAfter(DateTimeOffset.UtcNow);
                if (next is null) return WorkspaceActionResult.Fail("That cron expression never fires.");
                trigger.Cron = cron.Expression;
                trigger.NextDueAt = next;
            }
            else if (spec.EveryMinutes is { } minutes && minutes > 0)
            {
                var seconds = (int)Math.Round(minutes * 60);
                if (seconds < _opts.MinScheduleIntervalSeconds)
                {
                    return WorkspaceActionResult.Fail($"Schedules can run at most every {_opts.MinScheduleIntervalSeconds} seconds.");
                }

                trigger.IntervalSeconds = seconds;
                trigger.NextDueAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            }
            else
            {
                return WorkspaceActionResult.Fail("A schedule needs every_minutes or a cron expression.");
            }
        }
        else
        {
            trigger.Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        }

        S.Triggers[trigger.TriggerId] = trigger;
        var path = trigger.Kind == TriggerKind.Webhook ? WebhookPath(trigger) : null;
        AppendChat(ChatAuthorKind.System, "system", "Workspace", trigger.Kind switch
        {
            TriggerKind.Webhook => $"Webhook '{trigger.Name}' created: each delivery starts a run with its payload. Point your service at: POST {path}  (keep this URL secret).",
            TriggerKind.Watch => $"Watch '{trigger.Name}' created: {Describe(trigger)}, checking {trigger.SourceTool} for {WatchSummary(trigger)}. " +
                                 (trigger.WatchMode == "notify" ? "It runs without the LLM and only reports newly matching items." : "It runs without the LLM and starts a run only for newly matching items."),
            _ => $"Schedule '{trigger.Name}' created: runs the pipeline {Describe(trigger)}."
        });

        // Agents never see a webhook's secret; the API caller (the user) does.
        var result = WorkspaceActionResult.Ok($"Trigger '{name}' created.", JsonSerializer.Serialize(new
        {
            trigger_id = trigger.TriggerId,
            kind = trigger.Kind.ToString(),
            schedule = trigger.Kind is TriggerKind.Schedule or TriggerKind.Watch ? Describe(trigger) : null,
            note = trigger.Kind == TriggerKind.Webhook ? "The webhook URL has been shown to the user, who connects it to their service." : null,
            // The dry run lets the agent confirm its paths and conditions are right before relying on them.
            dry_run = preview
        }, ToolJson.Options));
        Remember(idempotencyKey, result);
        await SaveAsync();
        await AuditAsync(createdBy == "user" ? "user" : "agent", createdBy, createdBy == "user" ? "You" : createdBy, "trigger.added", trigger.Name, "ok",
            $"{trigger.Kind} '{trigger.Name}': {(trigger.Kind == TriggerKind.Webhook ? "on webhook" : Describe(trigger))}",
            JsonSerializer.Serialize(new { kind = trigger.Kind.ToString(), trigger.Instruction, trigger.SourceTool, watch = trigger.Kind == TriggerKind.Watch ? WatchSummary(trigger) : null }),
            key: $"trigger:{trigger.TriggerId}:added");

        if (trigger.Kind is TriggerKind.Schedule or TriggerKind.Watch)
        {
            await RegisterTriggerReminderAsync(trigger);
        }

        await ChangedAsync($"trigger {trigger.TriggerId} added");
        if (!revealSecret) return result;

        // The owner's (API) view: full trigger including a webhook's secret path, plus the dry run.
        var ownerView = JsonSerializer.SerializeToNode(ToView(trigger, includeSecret: true), OwnerJson)!.AsObject();
        if (preview is not null) ownerView["dry_run"] = JsonSerializer.SerializeToNode(preview, OwnerJson);
        return WorkspaceActionResult.Ok(result.Message, ownerView.ToJsonString());
    }

    public Task<WebhookOutcome> DeliverWebhookAsOwner(string triggerId, string body, string deliveryId) =>
        Exists && S.Triggers.TryGetValue(triggerId, out var trigger) && trigger.Kind == TriggerKind.Webhook && trigger.Secret is { } secret
            ? DeliverWebhook(new WebhookDelivery { TriggerId = triggerId, Token = secret, Body = body, DeliveryId = deliveryId, ContentType = "application/json" })
            : Task.FromResult(WebhookOutcome.NotFound);

    public async Task<WorkspaceActionResult> RemoveTrigger(string triggerId, string requestedBy)
    {
        if (!Exists || !S.Triggers.Remove(triggerId, out var trigger))
        {
            return WorkspaceActionResult.Ok("No such trigger (already removed).");
        }

        if (trigger.Kind is TriggerKind.Schedule or TriggerKind.Watch) await UnregisterTriggerReminderAsync(triggerId);
        AppendChat(ChatAuthorKind.System, "system", "Workspace", $"Trigger '{trigger.Name}' removed by {(requestedBy == "user" ? "you" : requestedBy)}.");
        await SaveAsync();
        await AuditAsync(requestedBy == "user" ? "user" : "agent", requestedBy, requestedBy == "user" ? "You" : requestedBy, "trigger.removed", trigger.Name, "ok",
            $"Removed {trigger.Kind} '{trigger.Name}'", key: $"trigger:{triggerId}:removed");
        await ChangedAsync($"trigger {triggerId} removed");
        return WorkspaceActionResult.Ok($"Trigger '{trigger.Name}' removed.");
    }

    public Task<IReadOnlyList<TriggerView>> ListTriggers() =>
        Task.FromResult<IReadOnlyList<TriggerView>>(S.Triggers.Values.Select(t => ToView(t, includeSecret: false)).ToList());

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName == OutboxReminder)
        {
            await ProcessNotificationOutbox();
            return;
        }

        if (!reminderName.StartsWith(TriggerReminderPrefix, StringComparison.Ordinal)) return;

        var triggerId = reminderName[TriggerReminderPrefix.Length..];
        if (!Exists || S.Status == WorkspaceStatus.Archived || !S.Triggers.TryGetValue(triggerId, out var trigger))
        {
            await UnregisterTriggerReminderAsync(triggerId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (S.Status == WorkspaceStatus.Active && trigger.Enabled)
        {
            // Id fixed by the tick: if this fire is repeated after a crash, the agent's mailbox
            // drops the copy.
            var tick = status.CurrentTickTime.ToUniversalTime().ToString("yyyyMMddHHmmss");
            if (trigger.Kind == TriggerKind.Watch)
            {
                await RunWatchAsync(trigger, tick);
            }
            else
            {
                await FireAsync(trigger, $"{S.WorkspaceId}-{trigger.TriggerId}-{tick}", "Schedule",
                    $"[Scheduled trigger '{trigger.Name}' fired at {now:u}]\n{trigger.Instruction}");
            }
        }

        if (trigger.Cron is not null)
        {
            // Cron times aren't a fixed period: re-arm the reminder for the next occurrence.
            trigger.NextDueAt = CronSchedule.Parse(trigger.Cron).NextAfter(now);
            if (trigger.NextDueAt is { } next) await RegisterTriggerReminderAsync(trigger, next - now);
        }
        else if (trigger.IntervalSeconds is { } interval)
        {
            trigger.NextDueAt = now.AddSeconds(interval);
        }

        await SaveAsync();
    }

    public async Task<WebhookOutcome> DeliverWebhook(WebhookDelivery delivery)
    {
        if (!Exists || !S.Triggers.TryGetValue(delivery.TriggerId, out var trigger) || trigger.Kind != TriggerKind.Webhook)
        {
            return WebhookOutcome.NotFound;
        }

        if (trigger.Secret is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(trigger.Secret), Encoding.UTF8.GetBytes(delivery.Token ?? string.Empty)))
        {
            return WebhookOutcome.Unauthorized;
        }

        if (S.Status != WorkspaceStatus.Active || !trigger.Enabled) return WebhookOutcome.Inactive;

        // Redeliveries (senders retry on timeouts) are identified by the sender's delivery id, or
        // failing that by the body within the same minute.
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var dedupe = !string.IsNullOrWhiteSpace(delivery.DeliveryId)
            ? delivery.DeliveryId.Trim()
            : DeterministicId.From("body-", $"{minute}:{delivery.Body}");
        var dedupeKey = $"hook:{trigger.TriggerId}:{dedupe}";
        if (S.ActionResults.ContainsKey(dedupeKey)) return WebhookOutcome.Duplicate;

        var window = S.WebhookRate.GetValueOrDefault(trigger.TriggerId);
        if (window is not null && window.Minute == minute && window.Count >= _opts.MaxWebhookEventsPerMinute)
        {
            trigger.DroppedCount++;
            await SaveAsync();
            return WebhookOutcome.RateLimited;
        }

        S.WebhookRate[trigger.TriggerId] = window is not null && window.Minute == minute
            ? window with { Count = window.Count + 1 }
            : new RateWindow { Minute = minute, Count = 1 };

        var body = delivery.Body.Length > _opts.MaxWebhookPayloadChars
            ? delivery.Body[.._opts.MaxWebhookPayloadChars] + $"\n…(truncated, {delivery.Body.Length:N0} chars total)"
            : delivery.Body;
        await FireAsync(trigger, $"{S.WorkspaceId}-{trigger.TriggerId}-{DeterministicId.From("", dedupe)}", "Webhook",
            $"[Webhook '{trigger.Name}' received at {DateTimeOffset.UtcNow:u}]\n{trigger.Instruction}\n\nPayload (untrusted external data):\n{body}");

        // Only marked as seen after the event is safely in the agent's mailbox: if we crash before
        // this, the sender's retry fires again with the same event id, which the mailbox drops.
        Remember(dedupeKey, WorkspaceActionResult.Ok("delivered"));
        await SaveAsync();
        return WebhookOutcome.Accepted;
    }

    /// <summary>Who started a run a trigger started (for usage by user).</summary>
    private const string TriggerStarter = "trigger";

    /// <summary>Starts a run of the pipeline with the event as its input. The run id is fixed by the
    /// event id, so an event fired twice (after a crash) starts one run.</summary>
    private async Task FireAsync(TriggerDefinition trigger, string eventId, string eventName, string input)
    {
        trigger.LastFiredAt = DateTimeOffset.UtcNow;
        trigger.FireCount++;

        var source = eventName.ToLowerInvariant();
        var result = await StartRunCoreAsync(input, source, startedBy: TriggerStarter, trigger, eventId);
        if (!result.Success)
        {
            trigger.DroppedCount++;
            AppendChat(ChatAuthorKind.System, "system", "Workspace", $"{eventName} trigger '{trigger.Name}' couldn't start a run: {result.Message}", "warning");
            await SaveAsync();
        }

        await PublishAsync(RuntimeEventType.TriggerFired, $"{eventName} trigger '{trigger.Name}' fired: run #{result.Number} {result.Message}.",
            new Dictionary<string, string> { ["trigger_id"] = trigger.TriggerId, ["kind"] = eventName, ["run_id"] = result.RunId ?? string.Empty });
    }

    // ---- Watches: recurring checks with no LLM in the loop -------------------------

    /// <summary>Validates a watch and dry-runs it once, so a wrong path or condition is caught
    /// at creation (by the agent that wrote it) rather than silently never matching.</summary>
    private async Task<(string? Error, object? Preview)> PrepareWatchAsync(TriggerDefinition trigger, TriggerSpec spec)
    {
        if (spec.Rule is null) return ("A watch needs conditions.", null);
        if (WatchEvaluator.Validate(spec.Rule) is { } ruleError) return (ruleError, null);
        if (string.IsNullOrWhiteSpace(spec.SourceTool)) return ("A watch needs a source_tool to call.", null);

        var source = await ResolveConnectionTool(spec.SourceTool.Trim());
        if (source is null) return ($"No enabled connection tool '{spec.SourceTool}'. Watches call a connection's tool, e.g. shop__get.", null);
        // Only reads: a watch runs unattended, forever, so it must never change anything.
        if (source.SideEffects != Tools.ToolSideEffects.ReadOnly) return ($"'{spec.SourceTool}' can change data; a watch may only call read-only tools.", null);

        // "wake_agent" is the name from before pipelines.
        var mode = spec.WatchMode?.ToLowerInvariant() is "run" or "wake_agent" ? "run" : "notify";
        trigger.Rule = spec.Rule;
        trigger.SourceTool = spec.SourceTool.Trim();
        trigger.SourceArgumentsJson = string.IsNullOrWhiteSpace(spec.SourceArgumentsJson) ? "{}" : spec.SourceArgumentsJson;
        trigger.WatchMode = mode;
        trigger.MessageTemplate = Clip(spec.MessageTemplate, 500, string.Empty) is { Length: > 0 } m ? m : null;
        trigger.Urgency = spec.Urgency?.ToLowerInvariant() is "info" or "urgent" ? spec.Urgency.ToLowerInvariant() : "warning";

        var result = await integrations.ExecuteToolAsync(S.WorkspaceId, source, WatchRequest(trigger, "dryrun"));
        if (!result.Success) return ($"Dry run of {trigger.SourceTool} failed: {result.ErrorMessage}", null);

        WatchEvaluation eval;
        try
        {
            eval = WatchEvaluator.Evaluate(result.ResultJson, trigger.Rule);
        }
        catch (FormatException ex)
        {
            return ($"The watch rule couldn't be applied: {ex.Message}", null);
        }

        return (null, new
        {
            items_found = eval.ItemCount,
            matching_now = eval.Matches.Count,
            sample = eval.Matches.Take(3).Select(x => x.Summary),
            warning = eval.ItemCount == 0 ? "items_path selected nothing in the current result — check the path." : null
        });
    }

    private async Task RunWatchAsync(TriggerDefinition trigger, string tick)
    {
        trigger.LastFiredAt = DateTimeOffset.UtcNow;
        trigger.Checks++;
        S.LlmCallsAvoided++;

        var source = trigger.SourceTool is null ? null : await ResolveConnectionTool(trigger.SourceTool);
        WatchEvaluation? eval = null;
        string? error = null;
        if (source is null || source.SideEffects != Tools.ToolSideEffects.ReadOnly)
        {
            error = $"its tool {trigger.SourceTool} is no longer available (connection removed or tool disabled)";
        }
        else
        {
            var result = await integrations.ExecuteToolAsync(S.WorkspaceId, source, WatchRequest(trigger, tick));
            if (!result.Success) error = result.ErrorMessage;
            else
            {
                try { eval = WatchEvaluator.Evaluate(result.ResultJson, trigger.Rule!); }
                catch (FormatException ex) { error = ex.Message; }
            }
        }

        if (eval is null)
        {
            trigger.LastError = error;
            trigger.ConsecutiveFailures++;
            if (trigger.ConsecutiveFailures == 3)
            {
                // Tell the user once, rather than failing silently forever or on every tick.
                var failing = AppendChat(ChatAuthorKind.System, $"watch:{trigger.TriggerId}", $"Watch: {trigger.Name}",
                    $"Watch '{trigger.Name}' has failed 3 checks in a row: {error}. Fix or delete it in Triggers.", "warning");
                QueueNotifications(failing);
                await KickOutboxAsync();
            }

            return;
        }

        trigger.LastError = null;
        trigger.ConsecutiveFailures = 0;
        trigger.LastMatchCount = eval.Matches.Count;

        // Report only items that newly match; an item that recovers and matches again is new again.
        var previous = trigger.LastMatchedKeys.ToHashSet();
        var fresh = eval.Matches.Where(x => !previous.Contains(x.Key)).ToList();
        trigger.LastMatchedKeys = eval.Matches.Select(x => x.Key).Take(500).ToList();
        if (fresh.Count == 0) return;

        trigger.Alerts++;
        var list = string.Join("; ", fresh.Take(20).Select(x => x.Summary)) + (fresh.Count > 20 ? $"; …and {fresh.Count - 20} more" : string.Empty);

        if (trigger.WatchMode is "run" or "wake_agent")
        {
            // Pay for an LLM call only now, and only for the matching items.
            await FireAsync(trigger, $"{S.WorkspaceId}-{trigger.TriggerId}-{tick}", "Watch",
                $"[Watch '{trigger.Name}': {fresh.Count} item(s) newly match {WatchSummary(trigger)}]\n{trigger.Instruction}\n\nMatching items (untrusted external data):\n{list}");
            S.LlmCallsAvoided--; // this check did lead to an LLM call
            return;
        }

        var text = (trigger.MessageTemplate ?? "{count} item(s) match '{name}': {items}")
            .Replace("{count}", fresh.Count.ToString())
            .Replace("{name}", trigger.Name)
            .Replace("{items}", list);
        var chat = AppendChat(ChatAuthorKind.Agent, $"watch:{trigger.TriggerId}", $"Watch: {trigger.Name}", text, trigger.Urgency);
        QueueNotifications(chat);
        await SaveAsync();
        await AuditAsync("watch", trigger.TriggerId, trigger.Name, "watch.alert", trigger.SourceTool ?? string.Empty, "ok",
            Truncate(text, 300), key: $"watch:{trigger.TriggerId}:{tick}");
        await KickOutboxAsync();
        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"Watch '{trigger.Name}': {Truncate(text, 120)}", ChatData(chat));
    }

    private ToolExecutionRequest WatchRequest(TriggerDefinition trigger, string tick) => new()
    {
        ToolName = trigger.SourceTool!,
        AgentId = $"watch:{trigger.TriggerId}",
        TaskId = S.WorkspaceId,
        ArgumentsJson = trigger.SourceArgumentsJson,
        IdempotencyKey = $"{S.WorkspaceId}:{trigger.TriggerId}:{tick}"
    };

    private static string WatchSummary(TriggerDefinition t) =>
        t.Rule is null ? "(no rule)" : string.Join(" and ", t.Rule.Conditions.Select(c => c.ToString()));

    private async Task RegisterTriggerReminderAsync(TriggerDefinition trigger, TimeSpan? dueTime = null)
    {
        TimeSpan due, period;
        if (trigger.Cron is not null)
        {
            due = dueTime ?? ((trigger.NextDueAt ?? DateTimeOffset.UtcNow.AddMinutes(1)) - DateTimeOffset.UtcNow);
            period = TimeSpan.FromDays(1); // Re-armed after every fire; this is only a fallback.
        }
        else
        {
            due = period = TimeSpan.FromSeconds(trigger.IntervalSeconds!.Value);
        }

        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);
        await this.RegisterOrUpdateReminder(TriggerReminderPrefix + trigger.TriggerId, due, period);
    }

    private async Task UnregisterTriggerReminderAsync(string triggerId)
    {
        var reminder = await this.GetReminder(TriggerReminderPrefix + triggerId);
        if (reminder is not null) await this.UnregisterReminder(reminder);
    }

    // ---- Integrations: connections -------------------------------------------

    public async Task<ConnectionResult> AddConnection(ConnectionRequest request)
    {
        if (!Exists) return ConnectionResult.Fail("No such workspace.");
        if (S.Status == WorkspaceStatus.Archived) return ConnectionResult.Fail("This workspace is archived.");
        if (S.Connections.Count >= _integrations.MaxConnectionsPerWorkspace)
        {
            return ConnectionResult.Fail($"A workspace can have at most {_integrations.MaxConnectionsPerWorkspace} connections.");
        }

        var plugin = plugins.Get(request.PluginId);
        if (plugin is null) return ConnectionResult.Fail($"No plugin '{request.PluginId}' is installed.");

        var name = ConnectionNames.Slug(string.IsNullOrWhiteSpace(request.Name) ? plugin.Manifest.Id : request.Name);
        if (S.Connections.Values.Any(c => c.Name == name))
        {
            return ConnectionResult.Fail($"This workspace already has a connection named '{name}'.");
        }

        // Only fields the plugin declares are kept, and each lands on the right side of the line:
        // secrets in the vault, settings in state.
        var settings = new Dictionary<string, string>();
        var secretValues = new Dictionary<string, string>();
        foreach (var field in plugin.Manifest.Settings)
        {
            var source = field.Secret ? request.Secrets : request.Settings;
            source.TryGetValue(field.Key, out var value);
            if (string.IsNullOrWhiteSpace(value)) value = field.Secret ? null : field.DefaultValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required) return ConnectionResult.Fail($"'{field.Label}' is required.");
                continue;
            }

            if (field.Secret) secretValues[field.Key] = value.Trim();
            else settings[field.Key] = value.Trim();
        }

        var definition = new ConnectionDefinition
        {
            ConnectionId = DeterministicId.FromOrNew("conn-", null),
            PluginId = plugin.Manifest.Id,
            Name = name,
            Settings = settings,
            SecretKeys = secretValues.Keys.ToList(),
            SupportsTools = plugin is Plugins.IToolProviderPlugin,
            SupportsNotifications = plugin is Plugins.INotificationChannelPlugin,
            SupportsInbound = plugin is Plugins.IInboundChannelPlugin,
            InboundSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant(),
            AllowedSenders = request.AllowedSenders.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList()
        };
        definition.NotifyLevel = request.NotifyLevel ?? (definition.SupportsNotifications ? NotifyLevel.Warning : NotifyLevel.Off);

        var scope = ConnectionNames.Scope(S.WorkspaceId, definition.ConnectionId);
        foreach (var (key, value) in secretValues)
        {
            await secretStore.PutAsync(scope, key, value);
        }

        var (check, tools) = await integrations.InspectAsync(S.WorkspaceId, definition);
        if (!check.Ok)
        {
            await secretStore.DeleteScopeAsync(scope);
            return ConnectionResult.Fail(check.Message);
        }

        definition.Tools = tools;
        S.Connections[definition.ConnectionId] = definition;

        var parts = new List<string> { $"Connected {plugin.Manifest.Name} as '{name}'." };
        if (tools.Count > 0) parts.Add($"{tools.Count(t => t.Enabled)} of {tools.Count} tools enabled for your agents.");
        if (definition.SupportsNotifications) parts.Add($"Notifications: {definition.NotifyLevel.ToString().ToLowerInvariant()}.");
        if (definition.SupportsInbound)
        {
            parts.Add(definition.AllowedSenders.Count == 0
                ? "Inbound messages are ignored until you add allowed senders."
                : $"Accepting commands from {string.Join(", ", definition.AllowedSenders)}.");
        }

        AppendChat(ChatAuthorKind.System, "system", "Workspace", string.Join(" ", parts));
        await SaveAsync();
        await AuditAsync("user", "user", "You", "connection.added", name, "ok",
            $"Connected {plugin.Manifest.Name} as '{name}' ({tools.Count} tools)",
            JsonSerializer.Serialize(new { plugin = plugin.Manifest.Id, settings, secret_keys = definition.SecretKeys, notify = definition.NotifyLevel.ToString() }),
            key: $"connection:{definition.ConnectionId}:added");
        await ChangedAsync($"connection {name} added");
        return ConnectionResult.Ok(ToView(definition), check.Message);
    }

    public async Task<ConnectionResult> UpdateConnection(string connectionId, ConnectionUpdate update)
    {
        if (!Exists || !S.Connections.TryGetValue(connectionId, out var c)) return ConnectionResult.Fail("No such connection.");

        if (update.NotifyLevel is { } level)
        {
            c.NotifyLevel = c.SupportsNotifications ? level : NotifyLevel.Off;
        }

        if (update.EnabledTools is { } enabled)
        {
            var set = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in c.Tools) t.Enabled = set.Contains(t.ExposedName) || set.Contains(t.LocalName);
        }

        if (update.AllowedSenders is { } senders)
        {
            c.AllowedSenders = senders.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        }

        if (update.Settings is { } changes)
        {
            if (plugins.Get(c.PluginId) is not { } plugin) return ConnectionResult.Fail($"The plugin '{c.PluginId}' isn't installed.");
            var merged = ConnectionSettings.Merge(plugin.Manifest, c.Settings, changes, out var invalid);
            if (invalid is not null) return ConnectionResult.Fail(invalid);

            // Checked like a new connection; nothing changes unless the new settings work.
            var candidate = new ConnectionDefinition
            {
                ConnectionId = c.ConnectionId, PluginId = c.PluginId, Name = c.Name, Settings = merged,
                SecretKeys = c.SecretKeys, Tools = c.Tools, InboundSecret = c.InboundSecret
            };
            var (check, tools) = await integrations.InspectAsync(S.WorkspaceId, candidate);
            if (!check.Ok) return ConnectionResult.Fail(check.Message);
            c.Settings = merged;
            c.Tools = tools;
            c.LastError = null;
        }

        if (update.Gateway is { } gateway)
        {
            if (!c.SupportsTools) return ConnectionResult.Fail("This connection has no tools to serve.");
            var known = c.Tools.Select(t => t.LocalName).ToHashSet(StringComparer.Ordinal);
            c.Gateway = new McpGatewaySettings { Enabled = gateway.Enabled, Tools = gateway.Tools.Where(known.Contains).Distinct().ToList() };
        }

        await SaveAsync();
        await AuditAsync("user", "user", "You", "connection.updated", c.Name, "ok",
            $"Updated '{c.Name}': notify {c.NotifyLevel}, {c.Tools.Count(t => t.Enabled)}/{c.Tools.Count} tools enabled" +
            (c.Gateway.Enabled ? $", MCP gateway on ({c.Gateway.Tools.Count} tools)" : string.Empty),
            JsonSerializer.Serialize(new { update.NotifyLevel, update.EnabledTools, update.AllowedSenders, settings = update.Settings?.Keys, update.Gateway }));
        await ChangedAsync($"connection {c.Name} updated");
        return ConnectionResult.Ok(ToView(c), "Updated.");
    }

    public async Task<ConnectionResult> RefreshConnectionTools(string connectionId)
    {
        if (!Exists || !S.Connections.TryGetValue(connectionId, out var c)) return ConnectionResult.Fail("No such connection.");

        var (check, tools) = await integrations.InspectAsync(S.WorkspaceId, c);
        c.LastError = check.Ok ? null : check.Message;
        if (check.Ok) c.Tools = tools;
        await SaveAsync();
        await ChangedAsync($"connection {c.Name} refreshed");
        return check.Ok ? ConnectionResult.Ok(ToView(c), $"{tools.Count} tools.") : ConnectionResult.Fail(check.Message);
    }

    public async Task RemoveConnection(string connectionId)
    {
        if (!Exists || !S.Connections.Remove(connectionId, out var c)) return;

        S.NotificationOutbox.RemoveAll(d => d.ConnectionId == connectionId);
        await secretStore.DeleteScopeAsync(ConnectionNames.Scope(S.WorkspaceId, connectionId));
        AppendChat(ChatAuthorKind.System, "system", "Workspace", $"Connection '{c.Name}' removed; its secrets were deleted.");
        await SaveAsync();
        await AuditAsync("user", "user", "You", "connection.removed", c.Name, "ok", $"Removed '{c.Name}' and deleted its secrets",
            key: $"connection:{connectionId}:removed");
        await ChangedAsync($"connection {c.Name} removed");
    }

    public async Task<WorkspaceDefinition?> ExportDefinition(bool includeSecrets)
    {
        if (!Exists) return null;
        var connections = new List<ConnectionRequest>();
        var withoutSecrets = new List<string>();
        foreach (var c in S.Connections.Values.OrderBy(c => c.CreatedAt))
        {
            var secrets = new Dictionary<string, string>();
            if (includeSecrets)
            {
                foreach (var key in c.SecretKeys)
                {
                    if (await secretStore.GetAsync(ConnectionNames.Scope(S.WorkspaceId, c.ConnectionId), key) is { Length: > 0 } value) secrets[key] = value;
                }
            }
            else if (c.SecretKeys.Count > 0)
            {
                withoutSecrets.Add(c.Name);
            }

            connections.Add(new ConnectionRequest
            {
                PluginId = c.PluginId,
                Name = c.Name,
                Settings = new Dictionary<string, string>(c.Settings),
                Secrets = secrets,
                NotifyLevel = c.NotifyLevel,
                AllowedSenders = [.. c.AllowedSenders]
            });
        }

        return new WorkspaceDefinition
        {
            Name = S.Name,
            Goal = S.Goal,
            TemplateId = S.TemplateId,
            Pipeline = S.Pipeline,
            SafetyPolicy = S.SafetyPolicy,
            DailyTokenLimit = S.DailyTokenLimit,
            DailyCostLimitUsd = S.DailyCostLimitUsd,
            Triggers = S.Triggers.Values.OrderBy(t => t.CreatedAt).Select(t => new TriggerSpec
            {
                Kind = t.Kind,
                Name = t.Name,
                Instruction = t.Instruction,
                EveryMinutes = t.IntervalSeconds is { } seconds ? seconds / 60.0 : null,
                Cron = t.Cron,
                Rule = t.Rule,
                SourceTool = t.SourceTool,
                SourceArgumentsJson = t.SourceArgumentsJson,
                WatchMode = t.WatchMode,
                MessageTemplate = t.MessageTemplate,
                Urgency = t.Urgency
            }).ToList(),
            Connections = connections,
            ConnectionsWithoutSecrets = withoutSecrets,
            DisabledTools = S.Connections.Values.Where(c => c.Tools.Any(t => !t.Enabled))
                .ToDictionary(c => c.Name, c => c.Tools.Where(t => !t.Enabled).Select(t => t.LocalName).ToList())
        };
    }

    public async Task<WorkspaceActionResult> Delete(string deletedBy)
    {
        if (!Exists) return WorkspaceActionResult.Ok("Already deleted.");
        if (S.Status != WorkspaceStatus.Archived) return WorkspaceActionResult.Fail("Archive the workspace before deleting it.");

        var name = S.Name;
        foreach (var connectionId in S.Connections.Keys.ToList())
        {
            await secretStore.DeleteScopeAsync(ConnectionNames.Scope(S.WorkspaceId, connectionId));
        }

        foreach (var trigger in S.Triggers.Values.ToList()) await UnregisterTriggerReminderAsync(trigger.TriggerId);
        await AuditAsync("user", deletedBy, deletedBy, "workspace.deleted", name, "ok", "Workspace deleted with its connections and secrets",
            key: $"workspace:{S.WorkspaceId}:deleted");
        await PublishAsync(RuntimeEventType.WorkspaceChanged, $"Workspace '{name}' deleted.", new Dictionary<string, string> { ["change"] = "deleted" });
        await state.ClearStateAsync();
        DeactivateOnIdle();
        return WorkspaceActionResult.Ok($"Workspace '{name}' deleted.");
    }

    public Task<IReadOnlyList<ConnectionView>> ListConnections() =>
        Task.FromResult<IReadOnlyList<ConnectionView>>(S.Connections.Values.Select(ToView).ToList());

    public Task<IReadOnlyList<ConnectionToolDescriptor>> GetConnectionTools()
    {
        if (!Exists || S.Status != WorkspaceStatus.Active) return Task.FromResult<IReadOnlyList<ConnectionToolDescriptor>>([]);

        return Task.FromResult<IReadOnlyList<ConnectionToolDescriptor>>(S.Connections.Values
            .SelectMany(c => c.Tools.Where(t => t.Enabled).Select(t => new ConnectionToolDescriptor
            {
                Name = t.ExposedName,
                Description = $"[{c.Name}, {c.PluginId}] {t.Description}",
                JsonSchema = t.JsonSchema,
                SideEffects = t.SideEffects
            }))
            .ToList());
    }

    public Task<ConnectionDefinition?> GetGatewayConnection(string connectionId) =>
        Task.FromResult(Exists && S.Status == WorkspaceStatus.Active && S.Connections.TryGetValue(connectionId, out var c) && c.SupportsTools && c.Gateway.Enabled
            ? c
            : null);

    public Task<ConnectionToolTarget?> ResolveConnectionTool(string exposedName)
    {
        foreach (var c in S.Connections.Values)
        {
            var tool = c.Tools.FirstOrDefault(t => t.Enabled && t.ExposedName == exposedName);
            if (tool is not null)
            {
                return Task.FromResult<ConnectionToolTarget?>(new ConnectionToolTarget { Connection = c, LocalName = tool.LocalName, SideEffects = tool.SideEffects });
            }
        }

        return Task.FromResult<ConnectionToolTarget?>(null);
    }

    public async Task<InboundResponseDto> HandleInbound(string connectionId, string token, InboundRequestDto request)
    {
        if (!Exists || !S.Connections.TryGetValue(connectionId, out var c) || !c.SupportsInbound ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.InboundSecret), Encoding.UTF8.GetBytes(token ?? string.Empty)))
        {
            return new InboundResponseDto { StatusCode = 404 };
        }

        if (S.Status == WorkspaceStatus.Archived) return new InboundResponseDto { StatusCode = 409 };

        Plugins.InboundResult parsed;
        try
        {
            parsed = await integrations.ParseInboundAsync(S.WorkspaceId, c, request);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Inbound message for connection {Connection} could not be parsed", c.Name);
            return new InboundResponseDto { StatusCode = 400 };
        }

        if (parsed.Rejected) return new InboundResponseDto { StatusCode = 403 };

        var reply = new InboundResponseDto { Body = parsed.ResponseBody, ContentType = parsed.ResponseContentType };
        if (!parsed.IsMessage || string.IsNullOrWhiteSpace(parsed.Text)) return reply;

        // Only the owner's own numbers/accounts may command the agents. Anyone else who learns
        // the number gets a normal empty reply and no reaction.
        if (parsed.SenderId is null || !c.AllowedSenders.Contains(parsed.SenderId, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Ignored inbound message from unauthorized sender {Sender} on connection {Connection}", parsed.SenderId, c.Name);
            await PublishAsync(RuntimeEventType.WorkspaceChanged, $"Ignored a message from an unapproved sender on '{c.Name}'.");
            return reply;
        }

        await AuditAsync("user", parsed.SenderId, parsed.SenderName ?? parsed.SenderId, "channel.message", c.Name, "ok",
            Truncate(parsed.Text, 200), key: $"inbound:{c.ConnectionId}:{parsed.MessageId ?? Guid.NewGuid().ToString("n")}");

        // "approve A7" / "reject A7 too expensive" from an allowed sender decides an approval.
        var command = ApprovalCommand().Match(parsed.Text.Trim());
        if (command.Success)
        {
            var approve = command.Groups[1].Value.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                          command.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var result = await DecideApproval(command.Groups[2].Value, approve,
                command.Groups[3].Value is { Length: > 0 } why ? why.Trim() : null, parsed.SenderId, c.Name);
            if (result.Success || S.Approvals.Values.Any(a => a.Code.Equals(command.Groups[2].Value, StringComparison.OrdinalIgnoreCase)))
            {
                return reply;
            }
        }

        var dedupe = parsed.MessageId is null ? null : $"{c.ConnectionId}:{parsed.MessageId}";
        await PostUserMessageCoreAsync(parsed.Text, null, dedupe, $"You (via {c.Name})");
        return reply;
    }

    // ---- Integrations: notification outbox -----------------------------------

    /// <summary>Queues a chat entry for every channel whose level covers its urgency. Only agent
    /// notices and explicit runtime warnings are forwarded — never system entries that may carry
    /// secrets (webhook and inbound URLs).</summary>
    private List<string> QueueNotifications(ChatEntry entry)
    {
        var names = new List<string>();
        foreach (var c in S.Connections.Values.Where(c => c.SupportsNotifications && Covers(c.NotifyLevel, entry.Urgency)))
        {
            S.NotificationOutbox.Add(new NotificationDelivery
            {
                DeliveryId = $"{S.WorkspaceId}-n{entry.Seq}-{c.ConnectionId}",
                ConnectionId = c.ConnectionId,
                AuthorName = entry.AuthorName,
                Text = entry.Text,
                Urgency = entry.Urgency
            });
            names.Add(c.Name);
        }

        return names;
    }

    private static bool Covers(NotifyLevel level, string urgency) => level switch
    {
        NotifyLevel.All => true,
        NotifyLevel.Warning => urgency is "warning" or "urgent",
        NotifyLevel.Urgent => urgency == "urgent",
        _ => false
    };

    private async Task KickOutboxAsync()
    {
        if (S.NotificationOutbox.Count == 0) return;

        // The reminder is the safety net: if this process dies before delivering, the outbox is
        // retried on a live silo.
        var period = durabilityOptions.Value.RecoveryReminderPeriod;
        await this.RegisterOrUpdateReminder(OutboxReminder, period, period);
        await this.AsReference<IWorkspaceGrain>().ProcessNotificationOutbox();
    }

    public async Task ProcessNotificationOutbox()
    {
        if (!Exists) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var delivery in S.NotificationOutbox.Where(d => d.NextAttemptAt <= now).ToList())
        {
            if (!S.Connections.TryGetValue(delivery.ConnectionId, out var c))
            {
                S.NotificationOutbox.Remove(delivery);
                continue;
            }

            var result = await integrations.SendNotificationAsync(S.WorkspaceId, S.Name, c, delivery);
            if (result.Delivered)
            {
                S.NotificationOutbox.Remove(delivery);
            }
            else
            {
                delivery.Attempts++;
                c.LastError = result.Error;
                if (!result.Retryable || delivery.Attempts >= _integrations.NotificationMaxAttempts)
                {
                    S.NotificationOutbox.Remove(delivery);
                    AppendChat(ChatAuthorKind.System, "system", "Workspace",
                        $"Couldn't deliver a notification through '{c.Name}' after {delivery.Attempts} attempt(s): {result.Error}", "warning");
                }
                else
                {
                    delivery.NextAttemptAt = now.AddSeconds(Math.Min(1800, _integrations.NotificationRetryBaseSeconds * Math.Pow(2, delivery.Attempts - 1)));
                }
            }

            // Saved after every delivery, so a crash mid-way doesn't resend the ones already sent.
            await SaveAsync();
        }

        if (S.NotificationOutbox.Count == 0)
        {
            var reminder = await this.GetReminder(OutboxReminder);
            if (reminder is not null) await this.UnregisterReminder(reminder);
        }
    }

    private ConnectionView ToView(ConnectionDefinition c) => new()
    {
        ConnectionId = c.ConnectionId,
        PluginId = c.PluginId,
        Name = c.Name,
        Settings = new Dictionary<string, string>(c.Settings),
        SecretKeys = c.SecretKeys.ToList(),
        NotifyLevel = c.NotifyLevel,
        Tools = c.Tools.Select(t => new ConnectionToolView { Name = t.ExposedName, Description = t.Description, SideEffects = t.SideEffects, Enabled = t.Enabled }).ToList(),
        CreatedAt = c.CreatedAt,
        LastError = c.LastError,
        AllowedSenders = c.AllowedSenders.ToList(),
        InboundPath = c.SupportsInbound ? integrations.InboundPath(S.WorkspaceId, c) : null,
        SupportsTools = c.SupportsTools,
        SupportsNotifications = c.SupportsNotifications,
        SupportsInbound = c.SupportsInbound,
        Gateway = c.Gateway,
        GatewayPath = c.SupportsTools ? $"/mcp/gateway/{S.WorkspaceId}/{c.ConnectionId}" : null
    };

    // ---- Safety: policy, approvals, audit ----------------------------------------

    public async Task<ToolCallPermission> CheckToolCall(ToolCallPermissionRequest request)
    {
        if (!Exists) return new ToolCallPermission { Decision = PolicyDecisionKind.Allow };

        // A call that already has an approval keeps it, even if the policy changed since.
        if (S.ApprovalByCallKey.TryGetValue(request.CallKey, out var existingId) && S.Approvals.TryGetValue(existingId, out var existing))
        {
            if (existing.Status == ApprovalStatus.Pending && DateTimeOffset.UtcNow > existing.ExpiresAt)
            {
                existing.Status = ApprovalStatus.Expired;
                existing.DecidedAt = DateTimeOffset.UtcNow;
                existing.DecidedBy = "system";
                existing.DecisionReason = $"no decision within {S.SafetyPolicy.ApprovalTimeoutHours}h";
                AppendChat(ChatAuthorKind.System, "system", "Workspace", $"Approval {existing.Code} expired: {existing.AgentName} won't run {existing.ToolName}.");
                await SaveAsync();
                await AuditAsync("system", "system", "Workspace", "approval.expired", existing.ToolName, "denied",
                    $"{existing.Code} expired", ApprovalDetail(existing), key: $"approval:{existing.ApprovalId}:expired");
            }

            return Permission(existing);
        }

        // The organization's policy applies on top of the workspace's: the stricter answer wins.
        var orgPolicy = await GrainFactory.GetGrain<Tenancy.ITenantGrain>(Tenancy.TenantIds.Normalize(S.TenantId)).GetSafetyPolicy();
        var verdict = PolicyEngine.Evaluate(orgPolicy, S.SafetyPolicy, request.ToolName, request.SideEffects);
        if (verdict.Decision == PolicyDecisionKind.Allow) return new ToolCallPermission { Decision = PolicyDecisionKind.Allow };

        var agent = await Registry.GetAsync(request.AgentId);
        var agentName = agent?.Role ?? request.AgentId;

        if (verdict.Decision == PolicyDecisionKind.Deny)
        {
            await AuditAsync("agent", request.AgentId, agentName, "tool.denied", request.ToolName, "denied",
                $"{agentName} was blocked from {request.ToolName} by {verdict.Reason}",
                System.Text.Json.JsonSerializer.Serialize(new { arguments = Truncate(request.ArgumentsJson, 2000), reason = verdict.Reason }),
                key: $"denied:{request.CallKey}");
            return new ToolCallPermission
            {
                Decision = PolicyDecisionKind.Deny,
                Message = verdict.ByOrganization
                    ? $"Blocked by {verdict.Reason}. Don't retry; tell the user if it matters."
                    : $"Blocked by the workspace's safety policy ({verdict.Reason}). Don't retry; tell the user if it matters."
            };
        }

        var approval = new ApprovalRecord
        {
            ApprovalId = DeterministicId.From("apr-", request.CallKey),
            Code = $"A{S.NextApprovalNumber++}",
            CallKey = request.CallKey,
            AgentId = request.AgentId,
            AgentName = agentName,
            ToolName = request.ToolName,
            SideEffects = request.SideEffects,
            ArgumentsJson = Truncate(request.ArgumentsJson, 4000),
            AgentNote = request.AgentNote is { Length: > 0 } note ? Truncate(note, 500) : null,
            PolicyReason = verdict.Reason,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, S.SafetyPolicy.ApprovalTimeoutHours))
        };
        S.Approvals[approval.ApprovalId] = approval;
        S.ApprovalByCallKey[request.CallKey] = approval.ApprovalId;
        TrimApprovals();

        var ask = AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Approval {approval.Code} needed: {agentName} wants to run {approval.ToolName} with {Truncate(approval.ArgumentsJson, 300)}" +
            (approval.AgentNote is null ? string.Empty : $" — \"{Truncate(approval.AgentNote, 200)}\"") +
            $". Reply \"approve {approval.Code}\" or \"reject {approval.Code} <reason>\".",
            // A person has to act, and the agent waits until they do: it reaches every channel.
            "urgent");
        QueueNotifications(ask);
        await SaveAsync();
        await KickOutboxAsync();
        await AuditAsync("agent", request.AgentId, agentName, "approval.requested", approval.ToolName, "pending",
            $"{approval.Code}: {agentName} asked to run {approval.ToolName} ({verdict.Reason})", ApprovalDetail(approval),
            key: $"approval:{approval.ApprovalId}:requested");
        await ChangedAsync($"approval {approval.Code} requested");
        return Permission(approval);
    }

    public Task<List<ApprovalRecord>> GetPendingApprovals() => Task.FromResult(Exists
        ? S.Approvals.Values.Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt > DateTimeOffset.UtcNow).OrderBy(a => a.RequestedAt).ToList()
        : []);

    public async Task<WorkspaceActionResult> DecideApproval(string approvalIdOrCode, bool approve, string? reason, string decidedBy, string channel)
    {
        var approval = S.Approvals.GetValueOrDefault(approvalIdOrCode)
                       ?? S.Approvals.Values.FirstOrDefault(a => a.Code.Equals(approvalIdOrCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (approval is null) return WorkspaceActionResult.Fail($"No approval '{approvalIdOrCode}'.");
        if (approval.Status != ApprovalStatus.Pending)
        {
            return WorkspaceActionResult.Fail($"Approval {approval.Code} was already {approval.Status.ToString().ToLowerInvariant()}.");
        }

        approval.Status = DateTimeOffset.UtcNow > approval.ExpiresAt ? ApprovalStatus.Expired : approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        approval.DecidedAt = DateTimeOffset.UtcNow;
        approval.DecidedBy = decidedBy;
        approval.DecisionReason = reason;

        var verb = approval.Status switch { ApprovalStatus.Approved => "approved", ApprovalStatus.Rejected => "rejected", _ => "expired before a decision" };
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Approval {approval.Code} {verb}{(channel == "chat" ? string.Empty : $" via {channel}")}: {approval.AgentName} " +
            $"{(approval.Status == ApprovalStatus.Approved ? "will now run" : "won't run")} {approval.ToolName}" +
            (reason is null ? "." : $" (\"{Truncate(reason, 200)}\")."));
        await SaveAsync();
        await AuditAsync("user", decidedBy, decidedBy == "user" ? "You" : decidedBy, $"approval.{approval.Status.ToString().ToLowerInvariant()}",
            approval.ToolName, approval.Status == ApprovalStatus.Approved ? "ok" : "denied",
            $"{approval.Code} {verb} via {channel}{(reason is null ? string.Empty : $": {reason}")}", ApprovalDetail(approval),
            key: $"approval:{approval.ApprovalId}:decided");

        // Wake the parked agent now; its recovery reminder covers the case where this is lost.
        await GrainFactory.GetGrain<IAgentGrain>(approval.AgentId).WakeAndThink();
        await ChangedAsync($"approval {approval.Code} {verb}");
        return WorkspaceActionResult.Ok($"Approval {approval.Code} {verb}.");
    }

    public Task<WorkspaceSafetyPolicy> GetSafetyPolicy() => Task.FromResult(S.SafetyPolicy);

    public async Task UpdateSafetyPolicy(WorkspaceSafetyPolicy policy, string changedBy)
    {
        if (!Exists) return;
        var before = S.SafetyPolicy;
        S.SafetyPolicy = policy with { ApprovalTimeoutHours = Math.Clamp(policy.ApprovalTimeoutHours, 1, 24 * 30), Rules = policy.Rules.Take(50).ToList() };
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Safety policy updated: {S.SafetyPolicy.Autonomy} mode, {S.SafetyPolicy.Rules.Count} rule(s)" +
            (S.SafetyPolicy.Team is { IsEmpty: false } ? ", team-shape rules set." : "."));
        await SaveAsync();
        await AuditAsync("user", changedBy, "You", "policy.updated", "safety policy", "ok",
            $"{before.Autonomy} → {S.SafetyPolicy.Autonomy}, {S.SafetyPolicy.Rules.Count} rule(s)",
            System.Text.Json.JsonSerializer.Serialize(new { before, after = S.SafetyPolicy }), key: $"policy:{Guid.NewGuid():n}");
        await ChangedAsync("safety policy updated");
    }

    private static ToolCallPermission Permission(ApprovalRecord a) => new()
    {
        Decision = PolicyDecisionKind.RequireApproval,
        ApprovalStatus = a.Status,
        ApprovalCode = a.Code,
        Message = a.Status switch
        {
            ApprovalStatus.Pending => $"Waiting for the user to approve {a.Code}.",
            ApprovalStatus.Approved => $"Approved by the user ({a.Code}).",
            ApprovalStatus.Rejected => $"The user rejected this action ({a.Code}){(a.DecisionReason is null ? "." : $": {a.DecisionReason.TrimEnd('.')}.")} Don't retry it unless the user asks.",
            _ => $"The approval request {a.Code} expired without a decision, so this action was not run. Ask the user if it's still needed."
        }
    };

    private void TrimApprovals()
    {
        const int keep = 500;
        foreach (var old in S.Approvals.Values.Where(a => a.Status != ApprovalStatus.Pending).OrderBy(a => a.RequestedAt).Take(Math.Max(0, S.Approvals.Count - keep)).ToList())
        {
            S.Approvals.Remove(old.ApprovalId);
            S.ApprovalByCallKey.Remove(old.CallKey);
        }
    }

    private static string ApprovalDetail(ApprovalRecord a) => System.Text.Json.JsonSerializer.Serialize(new
    {
        code = a.Code,
        tool = a.ToolName,
        side_effects = a.SideEffects.ToString(),
        arguments = a.ArgumentsJson,
        agent_note = a.AgentNote,
        policy = a.PolicyReason,
        decided_by = a.DecidedBy,
        reason = a.DecisionReason
    });

    /// <summary>Writes an audit record; a failure is logged, never allowed to break the action.</summary>
    private async Task AuditAsync(string actorType, string? actorId, string? actorName, string action, string target, string outcome,
        string summary, string detailJson = "{}", string? key = null)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Scope = S.WorkspaceId,
                Key = key ?? $"{action}:{Guid.NewGuid():n}",
                ActorType = actorType,
                ActorId = actorId ?? "unknown",
                ActorName = actorName ?? actorId ?? "unknown",
                Action = action,
                Target = target,
                Outcome = outcome,
                Summary = summary,
                DetailJson = detailJson
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed for {Action} in workspace {WorkspaceId}", action, S.WorkspaceId);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(approve|reject|yes|no)\s+(A\d+)\b\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ApprovalCommand();

    // ---- Budget -------------------------------------------------------------

    public Task<BudgetDecision> CheckBudget()
    {
        if (!Exists) return Task.FromResult(new BudgetDecision { Allowed = true });
        if (S.Status == WorkspaceStatus.Archived) return Task.FromResult(new BudgetDecision { Allowed = false, Reason = "the workspace is archived" });

        var today = Today();
        var tokens = S.UsageDay == today ? S.TokensToday : 0;
        var cost = S.UsageDay == today ? S.CostToday : 0;

        string? reason = null;
        if (tokens >= S.DailyTokenLimit) reason = $"the workspace's daily token budget ({S.DailyTokenLimit:N0}) is used up";
        else if (cost >= S.DailyCostLimitUsd) reason = $"the workspace's daily cost budget (${S.DailyCostLimitUsd:F2}) is used up";

        if (reason is not null && S.BudgetNoticeDay != today)
        {
            // Can't write state from an interleaved call; hand the notice to a normal turn.
            _ = this.AsReference<IWorkspaceGrain>().PostBudgetNotice(reason);
        }

        return Task.FromResult(new BudgetDecision { Allowed = reason is null, Reason = reason });
    }

    public async Task PostBudgetNotice(string reason)
    {
        var today = Today();
        if (!Exists || S.BudgetNoticeDay == today) return;

        S.BudgetNoticeDay = today;
        var notice = AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Agents are paused for today: {reason}. They continue after midnight UTC, or raise the daily budget.", "warning");
        QueueNotifications(notice);
        await SaveAsync();
        await KickOutboxAsync();
        await ChangedAsync("budget reached");
    }

    public async Task RecordUsage(string agentId, long tokens, decimal costUsd)
    {
        if (!Exists) return;

        var today = Today();
        if (S.UsageDay != today)
        {
            S.UsageDay = today;
            S.TokensToday = 0;
            S.CostToday = 0;
        }

        S.TokensToday += tokens;
        S.CostToday += costUsd;
        S.TotalTokens += tokens;
        S.TotalCostUsd += costUsd;

        // One early warning a day, so the user can raise the budget before agents stop.
        var tokenShare = S.DailyTokenLimit > 0 ? (double)S.TokensToday / S.DailyTokenLimit : 0;
        var costShare = S.DailyCostLimitUsd > 0 ? (double)(S.CostToday / S.DailyCostLimitUsd) : 0;
        if (Math.Max(tokenShare, costShare) >= BudgetWarningShare && Math.Max(tokenShare, costShare) < 1 && S.BudgetWarningDay != today)
        {
            S.BudgetWarningDay = today;
            var used = tokenShare >= costShare
                ? $"{S.TokensToday:N0} of {S.DailyTokenLimit:N0} tokens"
                : $"${S.CostToday:F2} of ${S.DailyCostLimitUsd:F2}";
            var warning = AppendChat(ChatAuthorKind.System, "system", "Workspace",
                $"{Math.Floor(Math.Max(tokenShare, costShare) * 100)}% of today's budget is used ({used}). Agents pause when it runs out, " +
                "until midnight UTC; raise the daily budget if today's work needs more.", "warning");
            QueueNotifications(warning);
            await SaveAsync();
            await KickOutboxAsync();
            await ChangedAsync("budget warning");
            return;
        }

        await SaveAsync();
    }

    /// <summary>Share of the daily budget at which the user is warned.</summary>
    private const double BudgetWarningShare = 0.8;

    public Task<WorkspacePolicy> GetPolicy() => Task.FromResult(BuildPolicy());

    private WorkspacePolicy BuildPolicy() => new()
    {
        WorkerBudget = new ResourceBudget
        {
            MaxTokens = _opts.WorkerTokens,
            MaxToolCalls = _opts.WorkerToolCalls,
            MaxCostUsd = _opts.WorkerCostUsd,
            // Helpers do their one job themselves. Work that turns out bigger goes back to the stage
            // that started them as a partial result, rather than growing a tree of helpers.
            MaxChildren = 0,
            MaxDurationSeconds = _opts.WorkerMaxDurationSeconds
        },
        MaxAgents = _opts.MaxAgentsPerWorkspace,
        MaxSpawnsPerRequest = _opts.MaxSpawnsPerRequest,
        TokensLeftToday = Math.Max(0, S.DailyTokenLimit - (S.UsageDay == Today() ? S.TokensToday : 0)),
        CostLeftTodayUsd = Math.Max(0, S.DailyCostLimitUsd - (S.UsageDay == Today() ? S.CostToday : 0)),
        Status = S.Status,
        Team = S.SafetyPolicy.Team
    };

    // ---- Snapshot -----------------------------------------------------------

    public async Task<WorkspaceSnapshot?> GetSnapshot()
    {
        if (!Exists) return null;

        // Recent runs and their agents (stages and helpers), for the live team view; older runs'
        // agents are on the runs' own pages.
        var entries = await RecentRunAgentsAsync();
        var agents = await Task.WhenAll(entries.Select(async e =>
        {
            AgentSnapshot? snap = null;
            try { snap = await orchestrator.GetSnapshotAsync(e.AgentId); }
            catch (Exception ex) { logger.LogDebug(ex, "Snapshot of {AgentId} failed", e.AgentId); }

            return new WorkspaceAgentView
            {
                AgentId = e.AgentId,
                Role = e.Role,
                Goal = e.Goal,
                Status = e.Status.ToString(),
                ParentAgentId = e.ParentAgentId,
                TokensUsed = snap?.Usage.TokensUsed ?? 0,
                CostUsd = snap?.Usage.CostUsd ?? 0,
                CurrentTask = snap?.CurrentTask,
                CachedInputTokens = snap?.Usage.CachedInputTokens ?? 0,
                CreatedAt = snap?.CreatedAt,
                CompletedAt = snap?.CompletedAt,
                PauseReason = snap?.PauseReason,
                PausedUntil = snap?.PausedUntil
            };
        }));

        var today = Today();
        return new WorkspaceSnapshot
        {
            WorkspaceId = S.WorkspaceId,
            TenantId = Tenancy.TenantIds.Normalize(S.TenantId),
            Name = S.Name,
            Goal = S.Goal,
            Status = S.Status,
            CreatedAt = S.CreatedAt,
            UpdatedAt = S.UpdatedAt,
            Conversation = S.Conversation.ToList(),
            Triggers = S.Triggers.Values.Select(t => ToView(t, includeSecret: false)).ToList(),
            Agents = agents.ToList(),
            DailyTokenLimit = S.DailyTokenLimit,
            DailyCostLimitUsd = S.DailyCostLimitUsd,
            TokensToday = S.UsageDay == today ? S.TokensToday : 0,
            CostToday = S.UsageDay == today ? S.CostToday : 0,
            TotalTokens = S.TotalTokens,
            TotalCostUsd = S.TotalCostUsd,
            Connections = S.Connections.Values.Select(ToView).ToList(),
            PendingNotifications = S.NotificationOutbox.Count,
            LlmCallsAvoided = S.LlmCallsAvoided,
            SafetyPolicy = S.SafetyPolicy,
            TemplateId = S.TemplateId,
            Pipeline = S.Pipeline,
            Runs = S.Runs.AsEnumerable().Reverse().Take(50).ToList(),
            QueuedRuns = S.RunQueue.Count,
            Approvals = S.Approvals.Values
                .OrderBy(a => a.Status == ApprovalStatus.Pending ? 0 : 1)
                .ThenByDescending(a => a.RequestedAt)
                .Take(50)
                .ToList()
        };
    }

    // ---- Helpers ------------------------------------------------------------

    private static bool IsTerminal(AgentStatus status) =>
        status is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut;

    private ChatEntry AppendChat(ChatAuthorKind kind, string authorId, string authorName, string text, string urgency = "info")
    {
        var entry = new ChatEntry
        {
            Seq = S.NextChatSeq++,
            AuthorKind = kind,
            AuthorId = authorId,
            AuthorName = authorName,
            Text = text,
            Urgency = urgency
        };
        S.Conversation.Add(entry);
        if (S.Conversation.Count > _opts.MaxConversationEntries)
        {
            S.Conversation.RemoveRange(0, S.Conversation.Count - _opts.MaxConversationEntries);
        }

        return entry;
    }

    private bool TryReplay(string idempotencyKey, out WorkspaceActionResult result)
    {
        result = null!;
        return !string.IsNullOrEmpty(idempotencyKey) && S.ActionResults.TryGetValue(idempotencyKey, out result!);
    }

    private void Remember(string key, WorkspaceActionResult result)
    {
        if (string.IsNullOrEmpty(key)) return;
        S.ActionResults[key] = result;
        S.ActionResultOrder.Add(key);
        while (S.ActionResultOrder.Count > ActionResultCacheSize)
        {
            S.ActionResults.Remove(S.ActionResultOrder[0]);
            S.ActionResultOrder.RemoveAt(0);
        }
    }

    private Task SaveAsync()
    {
        S.UpdatedAt = DateTimeOffset.UtcNow;
        return state.WriteStateAsync();
    }

    private async Task ChangedAsync(string what)
    {
        await PublishAsync(RuntimeEventType.WorkspaceChanged, $"Workspace '{S.Name}' {what}.");
        await ArchiveAsync();
    }

    private async Task ArchiveAsync()
    {
        try
        {
            var snapshot = await GetSnapshot();
            if (snapshot is not null) await archive.SaveAsync(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to archive workspace {WorkspaceId}", S.WorkspaceId);
        }
    }

    private string WebhookPath(TriggerDefinition t) => $"/api/hooks/{S.WorkspaceId}/{t.TriggerId}/{t.Secret}";

    private TriggerView ToView(TriggerDefinition t, bool includeSecret) => new()
    {
        TriggerId = t.TriggerId,
        Kind = t.Kind,
        Name = t.Name,
        Instruction = t.Instruction,
        IntervalSeconds = t.IntervalSeconds,
        Cron = t.Cron,
        WebhookPath = includeSecret && t.Kind == TriggerKind.Webhook ? WebhookPath(t) : null,
        Enabled = t.Enabled,
        LastFiredAt = t.LastFiredAt,
        FireCount = t.FireCount,
        NextDueAt = t.NextDueAt,
        CreatedBy = t.CreatedBy,
        DroppedCount = t.DroppedCount,
        WatchSummary = t.Kind == TriggerKind.Watch ? $"{t.SourceTool}: {WatchSummary(t)} → {t.WatchMode}" : null,
        Checks = t.Checks,
        Alerts = t.Alerts,
        LastMatchCount = t.LastMatchCount,
        LastError = t.LastError
    };

    private static string Describe(TriggerDefinition t) =>
        t.Cron is not null ? $"cron '{t.Cron}' (UTC)"
        : t.IntervalSeconds is { } s ? (s % 60 == 0 ? $"every {s / 60} minute(s)" : $"every {s} second(s)")
        : "on webhook";

    private static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    private static string Clip(string? value, int max, string fallback)
    {
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0) return fallback;
        return v.Length > max ? v[..max] : v;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private static Dictionary<string, string> ChatData(ChatEntry c) => new()
    {
        ["seq"] = c.Seq.ToString(),
        ["author_kind"] = c.AuthorKind.ToString(),
        ["author_id"] = c.AuthorId,
        ["author_name"] = c.AuthorName,
        ["text"] = c.Text,
        ["urgency"] = c.Urgency
    };

    private ValueTask PublishAsync(RuntimeEventType type, string summary, Dictionary<string, string>? data = null, string? agentId = null) =>
        events.PublishAsync(new RuntimeEvent
        {
            Type = type,
            TaskId = S.WorkspaceId,
            TenantId = Tenancy.TenantIds.Normalize(S.TenantId),
            AgentId = agentId,
            Summary = summary,
            Data = data ?? new Dictionary<string, string>()
        });
}
