using AgentRuntime.Agents;
using AgentRuntime.Events;
using AgentRuntime.LLM;
using AgentRuntime.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<AutonomyOptions>(configuration.GetSection(AutonomyOptions.SectionName));
        services.Configure<SupervisionOptions>(configuration.GetSection(SupervisionOptions.SectionName));

        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventStream>(sp => sp.GetRequiredService<InMemoryEventBus>());

        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
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

        services.AddSingleton<IAgentPromptBuilder, AgentPromptBuilder>();
        services.AddSingleton<ISystemPromptSection, RoleSection>();
        services.AddSingleton<ISystemPromptSection, GoalSection>();
        services.AddSingleton<ISystemPromptSection, CurrentStateSection>();
        services.AddSingleton<ISystemPromptSection, CapabilitiesSection>();
        services.AddSingleton<ISystemPromptSection, ToolsSection>();
        services.AddSingleton<ISystemPromptSection, ResourceLimitsSection>();
        services.AddSingleton<ISystemPromptSection, MessagingRulesSection>();
        services.AddSingleton<ISystemPromptSection, SpawningRulesSection>();
        services.AddSingleton<ISystemPromptSection, CompletionCriteriaSection>();
        services.AddSingleton<ISystemPromptSection, BehavioralRulesSection>();
        services.AddSingleton<ISystemPromptSection, EnvironmentInfoSection>();

        return services;
    }
}
