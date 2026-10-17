using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Infrastructure.Tasks;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Solutions;

public sealed class SolutionOptions
{
    public const string SectionName = "Solutions";

    /// <summary>How often the record-change outbox is checked.</summary>
    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Webhook deliveries of one change before it's given up on.</summary>
    public int MaxWebhookAttempts { get; set; } = 8;

    /// <summary>Longest an end-user token may live.</summary>
    public TimeSpan MaxEndUserTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Delivers the record-change outbox (docs/solutions.md#record-triggers): each change goes to every
/// workspace of its solution (whose record triggers decide whether to start a run, once per change)
/// and to the builder's webhooks, signed. Delivery is at least once: a change stays in the outbox
/// until every webhook took it, and workspaces drop the repeats.
/// </summary>
public sealed class RecordChangeDispatcher(
    IDbContextFactory<AgentDbContext> dbFactory,
    IGrainFactory grains,
    ISolutionCatalog catalog,
    IHttpClientFactory httpFactory,
    SecretProtector protector,
    IOptions<SolutionOptions> options,
    IOptions<TaskCallbackOptions> callbackOptions,
    TimeProvider clock,
    ILogger<RecordChangeDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The secret a solution's webhooks are signed with (shown to Admins with the install).</summary>
    public static string WebhookSecret(SecretProtector protector, string installId) =>
        "whsec_" + Convert.ToHexString(protector.Sign("solution-webhook-secret/v1", installId)).ToLowerInvariant()[..40];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                delivered = await DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Record change dispatch failed; retrying");
            }

            // A full batch: there's more waiting.
            if (delivered < 100)
            {
                try
                {
                    await Task.Delay(options.Value.DispatchInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var due = await db.SolutionRecordChanges
            .Where(c => c.DispatchedAt == null && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
            .OrderBy(c => c.ChangeId)
            .Take(100)
            .ToListAsync(ct);

        foreach (var row in due)
        {
            var change = new RecordChange
            {
                ChangeId = row.ChangeId,
                TenantId = row.TenantId,
                InstallId = row.InstallId,
                Entity = row.Entity,
                RecordId = row.RecordId,
                Event = row.Event,
                DataJson = row.DataJson,
                PreviousJson = row.PreviousJson,
                At = row.At,
                ChangedBy = row.ChangedBy,
                TaskId = row.TaskId
            };

            var solution = await catalog.GetAsync(row.TenantId, row.InstallId, ct);
            if (solution is { Status: SolutionInstallStatus.Installing }) continue; // its workspaces aren't all there yet
            if (solution is not { Status: SolutionInstallStatus.Active })
            {
                row.DispatchedAt = now;
                continue;
            }

            // Workspaces drop a change they've seen (by change id); this only saves sending it again
            // while a webhook is being retried.
            if (row.WorkspacesNotifiedAt is null)
            {
                foreach (var workspaceId in solution.Workspaces.Values)
                {
                    await grains.GetGrain<IWorkspaceGrain>(workspaceId).DeliverRecordChange(change);
                }

                row.WorkspacesNotifiedAt = now;
            }

            var failures = new List<string>();
            foreach (var hook in solution.Manifest.Webhooks.Where(h => Wants(h, change)))
            {
                try
                {
                    await SendAsync(solution, hook, change, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(ex.Message);
                }
            }

            row.Attempts++;
            if (failures.Count == 0 || row.Attempts >= options.Value.MaxWebhookAttempts)
            {
                row.DispatchedAt = now;
                row.LastError = failures.Count == 0 ? null : Clip("Gave up: " + string.Join("; ", failures));
                if (failures.Count > 0) logger.LogWarning("Gave up delivering record change {ChangeId} of {InstallId}: {Error}", row.ChangeId, row.InstallId, row.LastError);
            }
            else
            {
                row.LastError = Clip(string.Join("; ", failures));
                row.NextAttemptAt = now.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, row.Attempts - 1)));
            }
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private static string Clip(string s) => s.Length > 500 ? s[..500] : s;

    private static bool Wants(SolutionWebhook hook, RecordChange change) =>
        (hook.Events.Count == 0 || hook.Events.Contains("record.*") || hook.Events.Contains("record." + change.Event)) &&
        (hook.Entities.Count == 0 || hook.Entities.Contains(change.Entity));

    private async Task SendAsync(InstalledSolution solution, SolutionWebhook hook, RecordChange change, CancellationToken ct)
    {
        var url = hook.Url;
        if (!TaskCallbackDispatcher.IsAcceptableUrl(url, out var error)) throw new InvalidOperationException(error);
        var body = new JsonObject
        {
            ["event"] = "record." + change.Event,
            ["change_id"] = change.ChangeId,
            ["solution"] = solution.Manifest.Name,
            ["install_id"] = solution.InstallId,
            ["entity"] = change.Entity,
            ["record_id"] = change.RecordId,
            ["record"] = RecordSchema.ParseObject(change.DataJson),
            ["previous"] = RecordSchema.ParseObject(change.PreviousJson),
            ["changed_by"] = change.ChangedBy,
            ["at"] = change.At
        }.ToJsonString(Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Aktor-Event", "record." + change.Event);
        request.Headers.Add("Idempotency-Key", $"record-change-{solution.InstallId}-{change.ChangeId}");
        request.Headers.Add("X-Aktor-Signature", "sha256=" + TaskCallbackDispatcher.Sign(WebhookSecret(protector, solution.InstallId), body));
        var client = httpFactory.CreateClient(callbackOptions.Value.AllowPrivateNetworks ? TaskCallbackDispatcher.PrivateHttpClientName : TaskCallbackDispatcher.HttpClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{new Uri(url).Host} answered {(int)response.StatusCode}.");
    }
}
