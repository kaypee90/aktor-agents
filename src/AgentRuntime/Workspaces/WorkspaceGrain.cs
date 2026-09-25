using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Messaging;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace AgentRuntime.Workspaces;

/// <inheritdoc cref="IWorkspaceGrain"/>
public sealed class WorkspaceGrain(
    [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> state,
    IAgentOrchestrator orchestrator,
    IEventPublisher events,
    IWorkspaceArchive archive,
    IOptions<WorkspaceOptions> options,
    ILogger<WorkspaceGrain> logger) : Grain, IWorkspaceGrain, IRemindable
{
    private const string TriggerReminderPrefix = "trigger-";
    private const int ActionResultCacheSize = 2000;

    private readonly WorkspaceOptions _opts = options.Value;

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
        s.CreatedAt = DateTimeOffset.UtcNow;
        s.DailyTokenLimit = Math.Max(1_000, request.DailyTokenLimit ?? _opts.DefaultDailyTokenLimit);
        s.DailyCostLimitUsd = Math.Max(0.01m, request.DailyCostLimitUsd ?? _opts.DefaultDailyCostLimitUsd);
        s.CoordinatorAgentId = WorkspaceIds.CoordinatorId(s.WorkspaceId);

        AppendChat(ChatAuthorKind.User, "user", "You", s.Goal);
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Workspace created. Daily budget: {s.DailyTokenLimit:N0} tokens / ${s.DailyCostLimitUsd:F2}.");
        await SaveAsync();

        // The coordinator's first turn is the workspace goal itself.
        await orchestrator.CreateWorkspaceCoordinatorAsync(s.WorkspaceId, s.Name, s.Goal, BuildPolicy());

        await PublishAsync(RuntimeEventType.WorkspaceCreated, $"Workspace '{s.Name}' created.",
            new Dictionary<string, string> { ["name"] = s.Name, ["coordinator"] = s.CoordinatorAgentId });
        await ArchiveAsync();
    }

    public async Task Pause()
    {
        if (!Exists || S.Status != WorkspaceStatus.Active) return;

        S.Status = WorkspaceStatus.Paused;
        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace paused: triggers won't fire and agents are paused.");
        await SaveAsync();
        foreach (var agent in await LiveAgentsAsync())
        {
            await orchestrator.PauseAsync(agent.AgentId);
        }

        await ChangedAsync("paused");
    }

    public async Task Resume()
    {
        if (!Exists || S.Status != WorkspaceStatus.Paused) return;

        S.Status = WorkspaceStatus.Active;
        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace resumed.");
        await SaveAsync();
        // Unpause without forcing a turn: agents wake on their next message, schedule or webhook,
        // so resuming a big workspace doesn't cost one LLM call per agent.
        foreach (var agent in await LiveAgentsAsync())
        {
            await orchestrator.UnpauseAsync(agent.AgentId);
        }

        await ChangedAsync("resumed");
    }

    public async Task Archive()
    {
        if (!Exists || S.Status == WorkspaceStatus.Archived) return;

        S.Status = WorkspaceStatus.Archived;
        foreach (var trigger in S.Triggers.Values.Where(t => t.Kind == TriggerKind.Schedule))
        {
            await UnregisterTriggerReminderAsync(trigger.TriggerId);
        }

        AppendChat(ChatAuthorKind.System, "system", "Workspace", "Workspace archived: all agents retired and triggers removed.");
        await SaveAsync();
        foreach (var agent in await LiveAgentsAsync())
        {
            await orchestrator.RetireAsync(agent.AgentId, "the workspace was archived");
        }

        await ChangedAsync("archived");
    }

    public async Task UpdateBudget(int? dailyTokenLimit, decimal? dailyCostLimitUsd)
    {
        if (!Exists) return;
        if (dailyTokenLimit is { } t) S.DailyTokenLimit = Math.Max(1_000, t);
        if (dailyCostLimitUsd is { } c) S.DailyCostLimitUsd = Math.Max(0.01m, c);
        S.BudgetNoticeDay = null;
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Daily budget set to {S.DailyTokenLimit:N0} tokens / ${S.DailyCostLimitUsd:F2}.");
        await SaveAsync();
        await ChangedAsync("budget");
    }

    // ---- Conversation -------------------------------------------------------

    public async Task<ChatEntry> PostUserMessage(string text, string? toAgentId, string? clientMessageId)
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

        var target = S.CoordinatorAgentId;
        if (!string.IsNullOrWhiteSpace(toAgentId))
        {
            var entry = await Registry.GetAsync(toAgentId);
            if (entry is null || entry.RootAgentId != S.CoordinatorAgentId)
            {
                throw new ArgumentException($"No agent '{toAgentId}' in this workspace.");
            }

            target = toAgentId;
        }

        var chat = AppendChat(ChatAuthorKind.User, "user", "You", body);
        if (dedupeKey is not null) Remember(dedupeKey, WorkspaceActionResult.Ok("delivered"));
        await SaveAsync();

        // Durable delivery: the message id is fixed by the chat entry, so a retry of this call
        // can't reach the agent twice.
        await orchestrator.SendMessageAsync(new AgentMessage
        {
            MessageId = $"{S.WorkspaceId}-chat-{chat.Seq}",
            FromAgentId = "user",
            ToAgentId = target,
            MessageType = MessageType.TaskRequest,
            TaskId = S.WorkspaceId,
            Payload = body
        });

        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"User: {Truncate(body, 120)}", ChatData(chat));
        await ArchiveAsync();
        return chat;
    }

    public async Task<WorkspaceActionResult> Notify(string agentId, string text, string urgency, string idempotencyKey)
    {
        if (TryReplay(idempotencyKey, out var replayed)) return replayed;
        if (!Exists) return WorkspaceActionResult.Fail("No such workspace.");

        var body = Clip(text, _opts.MaxMessageLength, string.Empty);
        if (body.Length == 0) return WorkspaceActionResult.Fail("text must not be empty.");

        var level = urgency?.ToLowerInvariant() is "warning" or "urgent" ? urgency.ToLowerInvariant() : "info";
        var author = await Registry.GetAsync(agentId);
        var chat = AppendChat(ChatAuthorKind.Agent, agentId, author?.Role ?? agentId, body, level);
        var result = WorkspaceActionResult.Ok("The user has been notified.",
            JsonSerializer.Serialize(new { delivered = true, message_seq = chat.Seq }, ToolJson.Options));
        Remember(idempotencyKey, result);
        await SaveAsync();

        await PublishAsync(RuntimeEventType.WorkspaceMessage, $"{chat.AuthorName}: {Truncate(body, 120)}", ChatData(chat), agentId);
        await ArchiveAsync();
        return result;
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

        // Default target: whoever asked (an agent), else the coordinator.
        var target = !string.IsNullOrWhiteSpace(spec.TargetAgentId) ? spec.TargetAgentId.Trim()
            : createdBy == "user" ? S.CoordinatorAgentId : createdBy;
        var targetEntry = await Registry.GetAsync(target);
        if (targetEntry is null || targetEntry.RootAgentId != S.CoordinatorAgentId)
        {
            return WorkspaceActionResult.Fail($"No agent '{target}' in this workspace to receive the trigger.");
        }

        var trigger = new TriggerDefinition
        {
            TriggerId = DeterministicId.FromOrNew("trg-", string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey),
            Kind = spec.Kind,
            Name = name,
            TargetAgentId = target,
            Instruction = Clip(spec.Instruction, 1000, string.Empty),
            CreatedBy = createdBy
        };

        if (spec.Kind == TriggerKind.Schedule)
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
        AppendChat(ChatAuthorKind.System, "system", "Workspace", trigger.Kind == TriggerKind.Webhook
            ? $"Webhook '{trigger.Name}' created for {targetEntry.Role}. Point your service at: POST {path}  (keep this URL secret)."
            : $"Schedule '{trigger.Name}' created for {targetEntry.Role}: {Describe(trigger)}.");

        // Agents never see a webhook's secret; the API caller (the user) does.
        var result = WorkspaceActionResult.Ok($"Trigger '{name}' created.", JsonSerializer.Serialize(new
        {
            trigger_id = trigger.TriggerId,
            kind = trigger.Kind.ToString(),
            schedule = trigger.Kind == TriggerKind.Schedule ? Describe(trigger) : null,
            note = trigger.Kind == TriggerKind.Webhook ? "The webhook URL has been shown to the user, who connects it to their service." : null
        }, ToolJson.Options));
        Remember(idempotencyKey, result);
        await SaveAsync();

        if (trigger.Kind == TriggerKind.Schedule)
        {
            await RegisterTriggerReminderAsync(trigger);
        }

        await ChangedAsync($"trigger {trigger.TriggerId} added");
        return revealSecret
            ? WorkspaceActionResult.Ok(result.Message, JsonSerializer.Serialize(ToView(trigger, includeSecret: true), ToolJson.Options))
            : result;
    }

    public async Task<WorkspaceActionResult> RemoveTrigger(string triggerId, string requestedBy)
    {
        if (!Exists || !S.Triggers.Remove(triggerId, out var trigger))
        {
            return WorkspaceActionResult.Ok("No such trigger (already removed).");
        }

        if (trigger.Kind == TriggerKind.Schedule) await UnregisterTriggerReminderAsync(triggerId);
        AppendChat(ChatAuthorKind.System, "system", "Workspace", $"Trigger '{trigger.Name}' removed by {(requestedBy == "user" ? "you" : requestedBy)}.");
        await SaveAsync();
        await ChangedAsync($"trigger {triggerId} removed");
        return WorkspaceActionResult.Ok($"Trigger '{trigger.Name}' removed.");
    }

    public Task<IReadOnlyList<TriggerView>> ListTriggers() =>
        Task.FromResult<IReadOnlyList<TriggerView>>(S.Triggers.Values.Select(t => ToView(t, includeSecret: false)).ToList());

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
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
            await FireAsync(trigger, $"{S.WorkspaceId}-{trigger.TriggerId}-{tick}", "Schedule",
                $"[Scheduled trigger '{trigger.Name}' fired at {now:u}]\n{trigger.Instruction}");
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

    /// <summary>Wakes the trigger's agent. If that agent is gone, the coordinator gets the event
    /// instead, so a trigger never fires into the void.</summary>
    private async Task FireAsync(TriggerDefinition trigger, string eventId, string eventName, string payload)
    {
        var target = trigger.TargetAgentId;
        var entry = await Registry.GetAsync(target);
        if (entry is null || IsTerminal(entry.Status))
        {
            target = S.CoordinatorAgentId;
            payload = $"(The trigger's agent {trigger.TargetAgentId} is no longer running, so this came to you. " +
                      "Reassign or delete the trigger.)\n" + payload;
        }

        trigger.LastFiredAt = DateTimeOffset.UtcNow;
        trigger.FireCount++;

        await GrainFactory.GetGrain<IAgentGrain>(target).HandleEvent(new EnvironmentEvent
        {
            EventId = eventId,
            EventName = eventName,
            Payload = payload
        });

        await PublishAsync(RuntimeEventType.TriggerFired, $"{eventName} trigger '{trigger.Name}' fired for {target}.",
            new Dictionary<string, string> { ["trigger_id"] = trigger.TriggerId, ["kind"] = eventName, ["target"] = target }, target);
    }

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
        AppendChat(ChatAuthorKind.System, "system", "Workspace",
            $"Agents are paused for today: {reason}. They continue after midnight UTC, or raise the daily budget.", "warning");
        await SaveAsync();
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
        await SaveAsync();
    }

    public Task<WorkspacePolicy> GetPolicy() => Task.FromResult(BuildPolicy());

    private WorkspacePolicy BuildPolicy() => new()
    {
        StandingBudget = new ResourceBudget
        {
            MaxTokens = _opts.StandingAgentDailyTokens,
            MaxToolCalls = _opts.StandingAgentDailyToolCalls,
            MaxCostUsd = _opts.StandingAgentDailyCostUsd,
            MaxChildren = 20,
            MaxDurationSeconds = int.MaxValue,
            PeriodHours = 24
        },
        WorkerBudget = new ResourceBudget
        {
            MaxTokens = _opts.WorkerTokens,
            MaxToolCalls = _opts.WorkerToolCalls,
            MaxCostUsd = _opts.WorkerCostUsd,
            MaxChildren = 3,
            MaxDurationSeconds = _opts.WorkerMaxDurationSeconds
        },
        StandingContextWindow = _opts.StandingContextWindow,
        MaxAgents = _opts.MaxAgentsPerWorkspace,
        Status = S.Status
    };

    // ---- Snapshot -----------------------------------------------------------

    public async Task<WorkspaceSnapshot?> GetSnapshot()
    {
        if (!Exists) return null;

        var entries = await Registry.FindAsync(new FindAgentsQuery { RootAgentId = S.CoordinatorAgentId });
        var agents = await Task.WhenAll(entries.Select(async e =>
        {
            AgentSnapshot? snap = null;
            try { snap = await GrainFactory.GetGrain<IAgentGrain>(e.AgentId).GetSnapshot(); }
            catch (Exception ex) { logger.LogDebug(ex, "Snapshot of {AgentId} failed", e.AgentId); }

            return new WorkspaceAgentView
            {
                AgentId = e.AgentId,
                Role = e.Role,
                Goal = e.Goal,
                Status = e.Status.ToString(),
                ParentAgentId = e.ParentAgentId,
                Standing = snap?.Standing ?? false,
                TokensUsed = (snap?.Usage.LifetimeTokens ?? 0) + (snap?.Usage.TokensUsed ?? 0),
                CostUsd = (snap?.Usage.LifetimeCostUsd ?? 0) + (snap?.Usage.CostUsd ?? 0),
                CurrentTask = snap?.CurrentTask
            };
        }));

        var today = Today();
        return new WorkspaceSnapshot
        {
            WorkspaceId = S.WorkspaceId,
            Name = S.Name,
            Goal = S.Goal,
            Status = S.Status,
            CreatedAt = S.CreatedAt,
            UpdatedAt = S.UpdatedAt,
            CoordinatorAgentId = S.CoordinatorAgentId,
            Conversation = S.Conversation.ToList(),
            Triggers = S.Triggers.Values.Select(t => ToView(t, includeSecret: false)).ToList(),
            Agents = agents.ToList(),
            DailyTokenLimit = S.DailyTokenLimit,
            DailyCostLimitUsd = S.DailyCostLimitUsd,
            TokensToday = S.UsageDay == today ? S.TokensToday : 0,
            CostToday = S.UsageDay == today ? S.CostToday : 0,
            TotalTokens = S.TotalTokens,
            TotalCostUsd = S.TotalCostUsd
        };
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<IReadOnlyList<AgentDirectoryEntry>> LiveAgentsAsync() =>
        (await Registry.FindAsync(new FindAgentsQuery { RootAgentId = S.CoordinatorAgentId }))
        .Where(a => !IsTerminal(a.Status))
        .ToList();

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
        TargetAgentId = t.TargetAgentId,
        Instruction = t.Instruction,
        IntervalSeconds = t.IntervalSeconds,
        Cron = t.Cron,
        WebhookPath = includeSecret && t.Kind == TriggerKind.Webhook ? WebhookPath(t) : null,
        Enabled = t.Enabled,
        LastFiredAt = t.LastFiredAt,
        FireCount = t.FireCount,
        NextDueAt = t.NextDueAt,
        CreatedBy = t.CreatedBy,
        DroppedCount = t.DroppedCount
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
            AgentId = agentId,
            Summary = summary,
            Data = data ?? new Dictionary<string, string>()
        });
}
