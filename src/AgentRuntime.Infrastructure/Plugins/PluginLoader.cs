using System.Reflection;
using AgentRuntime.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Loads third-party plugins: every public, non-abstract <see cref="IAgentPlugin"/> in the DLLs of
/// the plugins folder (Plugins:Directory) is registered like a built-in one. Plugins are trusted
/// code — they run inside this process — so only install ones you trust, as with any package.
/// Assemblies load into the default context, so they share the SDK types with the host.
/// </summary>
public static class PluginLoader
{
    public static IReadOnlyList<string> AddPluginsFromDirectory(this IServiceCollection services, string? directory)
    {
        var loaded = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return loaded;

        foreach (var path in Directory.GetFiles(Path.GetFullPath(directory), "*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                continue; // Not a .NET assembly (a native dependency, say).
            }

            foreach (var type in SafeTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IAgentPlugin).IsAssignableFrom(t)))
            {
                services.AddSingleton(typeof(IAgentPlugin), type);
                loaded.Add($"{type.FullName} ({Path.GetFileName(path)})");
            }
        }

        return loaded;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
