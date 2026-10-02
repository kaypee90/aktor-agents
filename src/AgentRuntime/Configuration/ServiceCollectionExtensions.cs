using AgentRuntime.Agents;
using AgentRuntime.Events;
using AgentRuntime.LLM;
using AgentRuntime.Simulation;
using AgentRuntime.Tools;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentRuntime.Configuration;

/// <summary>
/// Wires up the provider-agnostic core of the runtime. LLM providers, persistence, and
/// infrastructure-bound tools are registered separately by the host (CLAUDE.md section 2: the
/// Agent runtime itself must not depend on any specific provider).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntimeCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RuntimeLimitsOptions>(configuration.GetSection(RuntimeLimitsOptions.SectionName));
        services.Configure<DefaultBudgetOptions>(configuration.GetSection(DefaultBudgetOptions.SectionName));
        services.Configure<Memory.MemoryOptions>(configuration.GetSection(Memory.MemoryOptions.SectionName));
        services.TryAddSingleton<Memory.IEmbeddingProvider, Memory.NullEmbeddingProvider>();
        services.Configure<Resources.PreviewOptions>(configuration.GetSection(Resources.PreviewOptions.SectionName));
        services.Configure<Safety.TeamPolicy>(configuration.GetSection(Safety.TeamPolicy.SectionName));
        services.Configure<TaskBudgetCeilingOptions>(configuration.GetSection(TaskBudgetCeilingOptions.SectionName));
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<AutonomyOptions>(configuration.GetSection(AutonomyOptions.SectionName));
        services.Configure<SupervisionOptions>(configuration.GetSection(SupervisionOptions.SectionName));
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.SectionName));
        services.Configure<Integrations.IntegrationsOptions>(configuration.GetSection(Integrations.IntegrationsOptions.SectionName));
        services.Configure<Workspaces.WorkspaceOptions>(configuration.GetSection(Workspaces.WorkspaceOptions.SectionName));
        services.Configure<Durability.DurabilityOptions>(configuration.GetSection(Durability.DurabilityOptions.SectionName));
        services.Configure<Tenancy.BillingOptions>(configuration.GetSection(Tenancy.BillingOptions.SectionName));

        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventStream>(sp => sp.GetRequiredService<InMemoryEventBus>());

        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

        // Step journal and replay (roadmap P6): in-memory by default, durable from the infrastructure layer.
        services.TryAddSingleton<Durability.IStepJournal, Durability.InMemoryStepJournal>();
        services.AddSingleton<Durability.RecordedLlmProvider>();
        services.AddSingleton<ToolRegistry>();

        // Governance tools: always available to every agent, independent of capabilities.
        services.AddSingleton<ITool, SpawnAgentTool>();
        services.AddSingleton<ITool, FindAgentsTool>();
        services.AddSingleton<ITool, SendMessageTool>();
        services.AddSingleton<ITool, GetAgentStatusTool>();
        services.AddSingleton<ITool, ListChildrenTool>();
        services.AddSingleton<ITool, CompleteTaskTool>();
        services.AddSingleton<ITool, ReadMemoryTool>();
        services.AddSingleton<ITool, WriteMemoryTool>();
        services.AddSingleton<ITool, SearchKnowledgeTool>();

        // Skills (docs/skills.md): written or uploaded by the organization, loaded by agents on demand.
        services.Configure<Skills.SkillOptions>(configuration.GetSection(Skills.SkillOptions.SectionName));
        services.TryAddSingleton<Skills.ISkillStore, Skills.InMemorySkillStore>();
        services.AddSingleton<ITool, Skills.LoadSkillTool>();
        services.AddSingleton<ITool, Skills.ReadSkillFileTool>();

        // Simulation: world actions for residents (never granted to task agents), genesis, and a
        // no-op archive that the infrastructure layer replaces with Postgres.
        services.AddWorldTools();
        services.AddSingleton<IWorldGenesis, LlmWorldGenesis>();
        services.TryAddSingleton<IWorldArchive, NullWorldArchive>();

        // Workspaces: long-running environments with triggers and a user channel.
        services.AddWorkspaceTools();
        services.TryAddSingleton<Workspaces.IWorkspaceArchive, Workspaces.NullWorkspaceArchive>();

        // Integrations: plugins (IAgentPlugin) are registered by the host — built-ins by the
        // infrastructure layer, third-party ones loaded from the plugins folder. ISecretStore must
        // also come from the host (encrypted, durable); there is deliberately no default.
        services.AddSingleton<Integrations.PluginCatalog>();

        // Safety: the audit log defaults to in-memory; the infrastructure layer makes it durable.
        services.TryAddSingleton<Safety.IAuditLog, Safety.InMemoryAuditLog>();
        services.AddSingleton<Integrations.IntegrationService>();

        services.AddSingleton<IAgentPromptBuilder, AgentPromptBuilder>();
        services.AddSingleton<ISystemPromptSection, RoleSection>();
        services.AddSingleton<ISystemPromptSection, GoalSection>();
        services.AddSingleton<ISystemPromptSection, ResidentPersonaSection>();
        services.AddSingleton<ISystemPromptSection, WorldRulesSection>();
        services.AddSingleton<ISystemPromptSection, Workspaces.WorkspaceSection>();
        services.AddSingleton<ISystemPromptSection, CurrentStateSection>();
        services.AddSingleton<ISystemPromptSection, CapabilitiesSection>();
        services.AddSingleton<ISystemPromptSection, ToolsSection>();
        services.AddSingleton<ISystemPromptSection, Skills.SkillsSection>();
        services.AddSingleton<ISystemPromptSection, ModelsSection>();
        services.AddSingleton<ISystemPromptSection, ResourceLimitsSection>();
        services.AddSingleton<ISystemPromptSection, MessagingRulesSection>();
        services.AddSingleton<ISystemPromptSection, SpawningRulesSection>();
        services.AddSingleton<ISystemPromptSection, CompletionCriteriaSection>();
        services.AddSingleton<ISystemPromptSection, BehavioralRulesSection>();
        services.Configure<PromptOptions>(configuration.GetSection(PromptOptions.SectionName));
        services.AddSingleton<ISystemPromptSection, OperatorInstructionsSection>();
        services.AddSingleton<ISystemPromptSection, EnvironmentInfoSection>();
        services.AddSingleton<ISystemPromptSection, ContextSummarySection>();
        services.AddSingleton<ContextCompactor>();
        services.AddSingleton<LlmSettingsService>();
        services.AddSingleton<ILlmSettingsResolver>(sp => sp.GetRequiredService<LlmSettingsService>());
        services.TryAddSingleton<ITaskModelSelection, InMemoryTaskModelSelection>();

        return services;
    }
}
