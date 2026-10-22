using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;

namespace AgentRuntime.Api.Platform;

/// <summary>
/// Ready-made solutions to start from (docs/solutions.md#templates): the packages under
/// <c>samples/solutions</c>, built into the API, so a new builder opens one in a click instead of
/// starting from a blank page. A template is an ordinary package: the builder edits a copy.
/// </summary>
public sealed class SolutionTemplates
{
    private const string ResourcePrefix = "solution-templates/";

    public sealed record TemplateView(string Name, string Title, string? Description, string Version, int Entities, int Workflows, bool Assistant, int Evals);

    private readonly Dictionary<string, (SolutionBundle Bundle, TemplateView View)> _templates = new(StringComparer.Ordinal);

    public SolutionTemplates(ILogger<SolutionTemplates> logger)
    {
        var assembly = typeof(SolutionTemplates).Assembly;
        var files = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var path = resource[ResourcePrefix.Length..].Replace('\\', '/');
            var slash = path.IndexOf('/');
            if (slash <= 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            (files.TryGetValue(path[..slash], out var f) ? f : files[path[..slash]] = new(StringComparer.Ordinal))[path[(slash + 1)..]] = reader.ReadToEnd();
        }

        foreach (var (folder, contents) in files)
        {
            if (!contents.Remove("aktor.yaml", out var manifestText)) continue;
            var bundle = new SolutionBundle { Manifest = manifestText, Files = contents };
            var (manifest, errors) = SolutionManifestReader.Read(bundle);
            if (manifest is null || errors.Count > 0)
            {
                logger.LogWarning("The template {Template} doesn't read: {Errors}", folder, string.Join("; ", errors));
                continue;
            }

            _templates[manifest.Name] = (bundle, new TemplateView(manifest.Name, manifest.DisplayTitle, manifest.Description?.Trim(), manifest.Version,
                manifest.Entities.Count, manifest.Workspaces.Count, manifest.Assistant is not null, manifest.Evals.Count));
        }
    }

    public IReadOnlyList<TemplateView> List() => _templates.Values.Select(t => t.View).OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();

    public SolutionBundle? Get(string name) => _templates.TryGetValue(name, out var t) ? t.Bundle : null;
}
