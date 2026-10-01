using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tasks;

public sealed class TaskCallbackOptions
{
    public const string SectionName = "Tasks:Callbacks";

    /// <summary>Webhooks to private addresses (localhost, the Docker network, 10.x…) are refused
    /// unless this is set: a caller could otherwise use them to probe the server's own network.
    /// Turn it on for a self-hosted setup where n8n runs next to Aktor.</summary>
    public bool AllowPrivateNetworks { get; set; }

    public int MaxAttempts { get; set; } = 6;

    /// <summary>How often undelivered webhooks are retried (they're also sent the moment a task finishes).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>What a completion webhook carries; also the shape callers get from the task service.</summary>
public sealed record TaskCallbackPayload(
    string Event,
    string TaskId,
    string Status,
    string? CorrelationId,
    string? DashboardUrl,
    DateTimeOffset? CompletedAt,
    string? Summary,
    JsonElement? Result);

/// <summary>
/// Delivers completion webhooks (docs/integrations-mcp.md). Durable: the task row records whether
/// its webhook was delivered, so one missed while the server was down is sent after it restarts.
/// Each POST is signed with HMAC-SHA256 of the body when the caller supplied a secret
/// (header <c>X-Aktor-Signature: sha256=&lt;hex&gt;</c>), and carries an
/// <c>Idempotency-Key</c> so a receiver can drop the redeliveries retries may cause.
/// </summary>
public sealed class TaskCallbackDispatcher(
    IDbContextFactory<AgentDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    TaskCompletionNotifier notifier,
    IOptions<TaskCallbackOptions> options,
    IOptions<TaskLinkOptions> links,
    ILogger<TaskCallbackDispatcher> logger) : BackgroundService
{
    public const string HttpClientName = "task-callbacks";
    public const string PrivateHttpClientName = "task-callbacks-private";
    public static string SecretScope(string taskId) => $"task:{taskId}";
    public const string SecretKey = "callback_secret";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        notifier.Completed += OnCompleted;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DeliverPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Task callback sweep failed; retrying later");
                }

                await _wake.WaitAsync(options.Value.SweepInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        finally
        {
            notifier.Completed -= OnCompleted;
        }
    }

    private void OnCompleted(string taskId) => _wake.Release();

    private async Task DeliverPendingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Tasks
            .Where(t => t.CallbackUrl != null && t.CompletedAt != null && t.CallbackDeliveredAt == null && t.CallbackAttempts < options.Value.MaxAttempts)
            .OrderBy(t => t.CompletedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var task in due)
        {
            // Exponential backoff between attempts: 0s, 30s, 1m, 2m, 4m…
            if (task.CallbackAttempts > 0 && task.CompletedAt is { } done &&
                DateTimeOffset.UtcNow - done < TimeSpan.FromSeconds(30 * Math.Pow(2, task.CallbackAttempts - 1)))
            {
                continue;
            }

            task.CallbackAttempts++;
            try
            {
                await SendAsync(task, ct);
                task.CallbackDeliveredAt = DateTimeOffset.UtcNow;
                task.CallbackLastError = null;
                logger.LogInformation("Delivered completion webhook for task {TaskId} (correlation {CorrelationId})", task.TaskId, task.CorrelationId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                task.CallbackLastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogWarning("Completion webhook for task {TaskId} failed (attempt {Attempt}): {Error}", task.TaskId, task.CallbackAttempts, ex.Message);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SendAsync(TaskRecord task, CancellationToken ct)
    {
        JsonElement? result = null;
        if (task.ResultJson is not null)
        {
            using var doc = JsonDocument.Parse(task.ResultJson);
            result = doc.RootElement.Clone();
        }

        var payload = new TaskCallbackPayload("task.completed", task.TaskId, task.Status, task.CorrelationId,
            links.Value.TaskUrl(task.TaskId), task.CompletedAt, task.ResultSummary, result);
        var body = JsonSerializer.Serialize(payload, Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, task.CallbackUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Aktor-Event", "task.completed");
        request.Headers.Add("Idempotency-Key", $"task-completed-{task.TaskId}");
        if (task.CorrelationId is not null) request.Headers.Add("X-Correlation-Id", task.CorrelationId);
        if (await secrets.GetAsync(SecretScope(task.TaskId), SecretKey, ct) is { Length: > 0 } secret)
        {
            request.Headers.Add("X-Aktor-Signature", "sha256=" + Sign(secret, body));
        }

        var client = httpFactory.CreateClient(options.Value.AllowPrivateNetworks ? PrivateHttpClientName : HttpClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The webhook answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    public static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>A callback URL the server will send to: absolute http(s), no credentials in it.</summary>
    public static bool IsAcceptableUrl(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "callback_url must be an absolute http or https URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "callback_url must not contain credentials; use callback_secret to authenticate deliveries.";
            return false;
        }

        return true;
    }
}

/// <summary>Where people look at a task: links returned with every result so a failure seen in
/// another system (n8n, Claude Code, a CrewAI crew) can be opened in the dashboard.</summary>
public sealed class TaskLinkOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>The dashboard's public URL, e.g. https://agents.example.com.</summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    public string TaskUrl(string taskId) => $"{BaseUrl.TrimEnd('/')}/?task={Uri.EscapeDataString(taskId)}";
    public string WorkspaceUrl(string workspaceId) => $"{BaseUrl.TrimEnd('/')}/workspaces?id={Uri.EscapeDataString(workspaceId)}";
}
