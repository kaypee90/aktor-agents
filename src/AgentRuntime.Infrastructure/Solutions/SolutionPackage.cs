using AgentRuntime.Solutions;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>A solution package on disk (a folder with aktor.yaml) as the bundle the API takes.
/// The CLI does the same in JavaScript (sdk/typescript/bin/aktor.mjs).</summary>
public static class SolutionPackage
{
    public static readonly string[] ManifestNames = ["aktor.yaml", "aktor.yml", "aktor.json"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yaml", ".yml", ".json", ".md", ".txt", ".csv", ".py", ".js", ".html", ".sql"
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", ".aktor" };

    public static SolutionBundle FromDirectory(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = ManifestNames.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists)
                           ?? throw new FileNotFoundException($"No aktor.yaml in {root}.");
        var files = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(part => part.StartsWith('.') || Skipped.Contains(part))) continue;
            if (path == manifestPath || !TextExtensions.Contains(Path.GetExtension(path))) continue;
            files[relative] = File.ReadAllText(path);
        }

        return new SolutionBundle { Manifest = File.ReadAllText(manifestPath), Files = files };
    }
}
