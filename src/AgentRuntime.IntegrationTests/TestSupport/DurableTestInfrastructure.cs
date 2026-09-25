using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Durability;
using AgentRuntime.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>
/// Stand-ins for Postgres in crash tests: grain state and reminders kept in process-wide (static)
/// stores, so they outlive a killed silo exactly as rows in a database would, while the killed
/// silo's in-memory activations, timers and caches are really gone. Stored state is serialized, so
/// a new activation reads a copy rather than sharing objects with the dead one. Stores are keyed
/// by cluster id so tests don't see each other's data.
/// </summary>
public sealed class DurableTestGrainStorage(Serializer serializer, IOptions<ClusterOptions> cluster) : IGrainStorage
{
    private static readonly ConcurrentDictionary<string, (byte[] Data, string ETag)> Store = new();

    private string Key(string stateName, GrainId grainId) => $"{cluster.Value.ClusterId}|{stateName}|{grainId}";

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (Store.TryGetValue(Key(stateName, grainId), out var entry))
        {
            grainState.State = serializer.Deserialize<T>(entry.Data);
            grainState.ETag = entry.ETag;
            grainState.RecordExists = true;
        }

        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var etag = Guid.NewGuid().ToString("n");
        Store[Key(stateName, grainId)] = (serializer.SerializeToArray(grainState.State), etag);
        grainState.ETag = etag;
        grainState.RecordExists = true;
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        Store.TryRemove(Key(stateName, grainId), out _);
        grainState.RecordExists = false;
        return Task.CompletedTask;
    }
}

public sealed class DurableTestReminderTable(IOptions<ClusterOptions> cluster) : IReminderTable
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<(GrainId, string), ReminderEntry>> Tables = new();

    private ConcurrentDictionary<(GrainId, string), ReminderEntry> Table => Tables.GetOrAdd(cluster.Value.ClusterId, _ => new());

    public Task<ReminderTableData> ReadRows(GrainId grainId) =>
        Task.FromResult(new ReminderTableData(Table.Values.Where(e => e.GrainId == grainId).ToList()));

    public Task<ReminderTableData> ReadRows(uint begin, uint end) =>
        Task.FromResult(new ReminderTableData(Table.Values.Where(e =>
        {
            var hash = e.GrainId.GetUniformHashCode();
            return begin < end ? hash > begin && hash <= end : hash > begin || hash <= end;
        }).ToList()));

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) =>
        Task.FromResult(Table.GetValueOrDefault((grainId, reminderName)));

    public Task<string?> UpsertRow(ReminderEntry entry)
    {
        entry.ETag = Guid.NewGuid().ToString("n");
        Table[(entry.GrainId, entry.ReminderName)] = entry;
        return Task.FromResult<string?>(entry.ETag);
    }

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) =>
        Task.FromResult(Table.TryRemove((grainId, reminderName), out _));

    public Task TestOnlyClearTable()
    {
        Table.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Records every execution (with its idempotency key) and can block one call forever, so
/// a test can kill the silo while that call is "in flight".</summary>
public static class TestSideEffects
{
    public static readonly ConcurrentQueue<(string Tool, string Key)> Executions = new();
    private static readonly ConcurrentDictionary<string, bool> BlockOnce = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Entered = new();

    public static void Reset()
    {
        Executions.Clear();
        BlockOnce.Clear();
        Entered.Clear();
    }

    /// <summary>The next call to <paramref name="tool"/> never returns (until its silo is killed).</summary>
    public static Task BlockNextCall(string tool)
    {
        BlockOnce[tool] = true;
        return Entered.GetOrAdd(tool, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    public static int Count(string tool) => Executions.Count(e => e.Tool == tool);

    internal static async Task<ToolExecutionResult> RunAsync(string tool, ToolExecutionRequest request)
    {
        Executions.Enqueue((tool, request.IdempotencyKey));
        if (BlockOnce.TryRemove(tool, out _))
        {
            Entered.GetOrAdd(tool, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await Task.Delay(Timeout.Infinite);
        }

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { done = tool, key = request.IdempotencyKey }));
    }
}

/// <summary>Stands in for a payment: must never run twice.</summary>
public sealed class ChargeCardTestTool : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "test_charge_card",
        Description = "Charge a card (test).",
        JsonSchema = """{ "type": "object", "properties": {} }""",
        SideEffects = ToolSideEffects.NonIdempotent
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) => TestSideEffects.RunAsync(Definition.Name, request);
}

/// <summary>Stands in for an API call that accepts an idempotency key: safe to repeat.</summary>
public sealed class SendReceiptTestTool : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "test_send_receipt",
        Description = "Send a receipt (test, idempotent).",
        JsonSchema = """{ "type": "object", "properties": {} }""",
        SideEffects = ToolSideEffects.Idempotent
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) => TestSideEffects.RunAsync(Definition.Name, request);
}

public static class DurableTestCluster
{
    /// <summary>An in-process cluster whose state and reminders survive silo kills, with fast
    /// recovery reminders (seconds, not the production minute).</summary>
    public static async Task<InProcessTestCluster> StartAsync(IDictionary<string, string?>? extraConfiguration = null)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddGrainStorage("Default", (sp, _) =>
                new DurableTestGrainStorage(sp.GetRequiredService<Serializer>(), sp.GetRequiredService<IOptions<ClusterOptions>>()));
            silo.AddReminders();
            silo.Services.AddSingleton<IReminderTable, DurableTestReminderTable>();
            silo.Services.Configure<ReminderOptions>(o =>
            {
                o.MinimumReminderPeriod = TimeSpan.FromSeconds(1);
                o.RefreshReminderListPeriod = TimeSpan.FromSeconds(1);
            });

            var settings = new Dictionary<string, string?>
            {
                ["Durability:RecoveryReminderPeriod"] = "00:00:02",
                // Second-scale schedules so tests run in seconds, not minutes.
                ["Workspaces:MinScheduleIntervalSeconds"] = "1"
            };
            foreach (var (k, v) in extraConfiguration ?? new Dictionary<string, string?>()) settings[k] = v;
            TestSiloConfigurator.AddRuntimeServices(silo.Services, settings);
            silo.Services.AddSingleton<ITool, ChargeCardTestTool>();
            silo.Services.AddSingleton<ITool, SendReceiptTestTool>();
        });

        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    /// <summary>Kills every silo abruptly — no deactivation, no graceful shutdown, like a power
    /// cut — then starts a fresh one that has only the durable stores to go on.</summary>
    public static async Task CrashAndRestartAsync(this InProcessTestCluster cluster)
    {
        foreach (var silo in cluster.Silos.ToList())
        {
            await cluster.KillSiloAsync(silo);
        }

        await cluster.StartAdditionalSiloAsync();
    }
}
