using AgentRuntime.Durability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;

namespace AgentRuntime.Infrastructure;

/// <summary>Silo settings (section "Silo").</summary>
public sealed class OrleansHostingOptions
{
    // Not "Orleans": Orleans itself reads that section for provider configuration, and our keys
    // there (e.g. Clustering) would be misread as an Orleans clustering provider.
    public const string SectionName = "Silo";

    /// <summary>"AdoNet" (default): grain state and reminders in PostgreSQL, so agents, mailboxes,
    /// worlds and recovery reminders survive restarts and crashes. "Memory": nothing survives a
    /// restart; only for throwaway demos and tests.</summary>
    public string Storage { get; set; } = "AdoNet";

    /// <summary>"Localhost" (default): a single silo. "AdoNet": cluster membership in PostgreSQL, so
    /// several silos can run and take over each other's agents when one dies.</summary>
    public string Clustering { get; set; } = "Localhost";

    public string ClusterId { get; set; } = "aktor";
    public string ServiceId { get; set; } = "aktor-agents";
    public int SiloPort { get; set; } = 11111;
    public int GatewayPort { get; set; } = 30000;

    public bool UsesAdoNetStorage => Storage.Equals("AdoNet", StringComparison.OrdinalIgnoreCase);
    public bool UsesAdoNetClustering => Clustering.Equals("AdoNet", StringComparison.OrdinalIgnoreCase);
}

public static class OrleansHosting
{
    private const string PostgresInvariant = "Npgsql";

    public static OrleansHostingOptions GetOrleansOptions(this IConfiguration configuration) =>
        configuration.GetSection(OrleansHostingOptions.SectionName).Get<OrleansHostingOptions>() ?? new OrleansHostingOptions();

    /// <summary>Configures storage, reminders and clustering for the agent runtime's silo.</summary>
    public static ISiloBuilder UseAgentRuntimeStorage(this ISiloBuilder silo, IConfiguration configuration)
    {
        var options = configuration.GetOrleansOptions();
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        var durability = configuration.GetSection(DurabilityOptions.SectionName).Get<DurabilityOptions>() ?? new DurabilityOptions();
        silo.Services.Configure<DurabilityOptions>(configuration.GetSection(DurabilityOptions.SectionName));
        // Orleans rejects reminder periods below its minimum; keep them consistent.
        silo.Services.Configure<ReminderOptions>(o =>
        {
            if (durability.RecoveryReminderPeriod < o.MinimumReminderPeriod) o.MinimumReminderPeriod = durability.RecoveryReminderPeriod;
        });

        // Orleans' ADO.NET providers resolve the driver by invariant name through DbProviderFactories.
        System.Data.Common.DbProviderFactories.RegisterFactory(PostgresInvariant, Npgsql.NpgsqlFactory.Instance);

        if (options.UsesAdoNetStorage)
        {
            silo.AddAdoNetGrainStorage("Default", builder => builder.Configure<Serializer>((o, serializer) =>
            {
                o.Invariant = PostgresInvariant;
                o.ConnectionString = connectionString;
                // Orleans' own binary format: version tolerant by [Id] number, so fields can be added
                // to agent state and old saved state still loads years later.
                o.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer);
            }));
            silo.UseAdoNetReminderService(o =>
            {
                o.Invariant = PostgresInvariant;
                o.ConnectionString = connectionString;
            });
        }
        else
        {
            silo.AddMemoryGrainStorage("Default");
            silo.UseInMemoryReminderService();
        }

        if (options.UsesAdoNetClustering)
        {
            silo.UseAdoNetClustering(o =>
            {
                o.Invariant = PostgresInvariant;
                o.ConnectionString = connectionString;
            });
            silo.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = options.ClusterId;
                o.ServiceId = options.ServiceId;
            });
            silo.ConfigureEndpoints(options.SiloPort, options.GatewayPort);
        }
        else
        {
            silo.UseLocalhostClustering(options.SiloPort, options.GatewayPort, serviceId: options.ServiceId, clusterId: options.ClusterId);
        }

        return silo;
    }
}
