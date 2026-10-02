using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Skills;

/// <summary>What every agent sees about a skill: enough to decide whether to load it.</summary>
public sealed record SkillSummary(string Name, string Description, int Version, bool Enabled, DateTimeOffset UpdatedAt, int FileCount);

/// <summary>A skill's resource file (reference docs, templates, scripts), stored as text.</summary>
public sealed record SkillFile(string Path, string Content);

/// <summary>
/// A skill, in the Agent Skills format: a SKILL.md whose frontmatter names and describes it and whose
/// body holds the instructions, plus optional resource files. Agents see the name and description
/// in their prompt and load the rest only when the skill is relevant, so a library of skills costs
/// a line each until it's used.
/// </summary>
public sealed record SkillDocument
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Instructions { get; init; }
    public List<SkillFile> Files { get; init; } = [];
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; init; }

    public SkillSummary Summary => new(Name, Description, Version, Enabled, UpdatedAt, Files.Count);
}

/// <summary>An organization's skills. Everything is scoped by tenant: a skill is never visible to
/// another organization's agents or users.</summary>
public interface ISkillStore
{
    Task<IReadOnlyList<SkillSummary>> ListAsync(string tenantId, bool enabledOnly, CancellationToken cancellationToken = default);
    Task<SkillDocument?> GetAsync(string tenantId, string name, CancellationToken cancellationToken = default);
    /// <summary>Creates the skill, or replaces it as a new version.</summary>
    Task<SkillDocument> SaveAsync(string tenantId, SkillDocument skill, CancellationToken cancellationToken = default);
    Task<bool> SetEnabledAsync(string tenantId, string name, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string tenantId, string name, CancellationToken cancellationToken = default);
}

public sealed class InMemorySkillStore : ISkillStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Name), SkillDocument> _skills = new();

    public Task<IReadOnlyList<SkillSummary>> ListAsync(string tenantId, bool enabledOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SkillSummary>>(_skills.Where(kv => kv.Key.Tenant == tenantId && (!enabledOnly || kv.Value.Enabled))
            .Select(kv => kv.Value.Summary).OrderBy(s => s.Name).ToList());

    public Task<SkillDocument?> GetAsync(string tenantId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_skills.GetValueOrDefault((tenantId, name)));

    public Task<SkillDocument> SaveAsync(string tenantId, SkillDocument skill, CancellationToken cancellationToken = default)
    {
        var saved = _skills.AddOrUpdate((tenantId, skill.Name), skill with { Version = 1 },
            (_, old) => skill with { Version = old.Version + 1, Enabled = old.Enabled });
        return Task.FromResult(saved);
    }

    public Task<bool> SetEnabledAsync(string tenantId, string name, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!_skills.TryGetValue((tenantId, name), out var s)) return Task.FromResult(false);
        _skills[(tenantId, name)] = s with { Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string tenantId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_skills.TryRemove((tenantId, name), out _));
}

public sealed class SkillPackageException(string message) : Exception(message);

/// <summary>Limits on an uploaded skill.</summary>
public sealed class SkillOptions
{
    public const string SectionName = "Skills";

    public int MaxFiles { get; set; } = 50;
    public int MaxTotalBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxInstructionsChars { get; set; } = 100_000;
    /// <summary>Skills listed in an agent's prompt (by name and description); the rest can still be loaded.</summary>
    public int MaxListedInPrompt { get; set; } = 40;
}

/// <summary>
/// Reads an uploaded skill: a single SKILL.md, or a .zip holding SKILL.md (at the root or in one
/// top-level folder) and resource files. Frontmatter gives the name (lowercase letters, digits,
/// hyphens; at most 64) and description (at most 1,024 characters). Only text files are kept;
/// paths can't escape the skill. Nothing in a skill is executed by the runtime.
/// </summary>
public static partial class SkillPackage
{
    public const string ManifestName = "SKILL.md";

    public static SkillDocument FromMarkdown(string markdown, SkillOptions? options = null) => Parse(markdown, [], options ?? new SkillOptions());

    /// <summary>A skill written in the editor rather than uploaded: the same rules as SKILL.md.</summary>
    public static SkillDocument FromParts(string name, string description, string instructions, IEnumerable<SkillFile>? files, SkillOptions? options = null)
    {
        var opts = options ?? new SkillOptions();
        var checkedFiles = new List<SkillFile>();
        long total = instructions.Length;
        foreach (var f in files ?? [])
        {
            var path = NormalizePath(f.Path ?? string.Empty)
                       ?? throw new SkillPackageException($"'{f.Path}' isn't a valid file path inside the skill.");
            if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) throw new SkillPackageException($"{ManifestName} is the skill itself; don't add it as a file.");
            if (checkedFiles.Any(x => x.Path == path)) throw new SkillPackageException($"Two files are named '{path}'.");
            total += f.Content?.Length ?? 0;
            checkedFiles.Add(new SkillFile(path, f.Content ?? string.Empty));
        }

        if (checkedFiles.Count > opts.MaxFiles) throw new SkillPackageException($"A skill can have at most {opts.MaxFiles} files.");
        if (total > opts.MaxTotalBytes) throw new SkillPackageException($"The skill is larger than {opts.MaxTotalBytes / 1024 / 1024} MB.");
        return Parse(ToMarkdown(name.Trim(), description.Trim().ReplaceLineEndings(" "), instructions), checkedFiles.OrderBy(f => f.Path).ToList(), opts);
    }

    /// <summary>The skill as a SKILL.md, for download and editing round trips.</summary>
    public static string ToMarkdown(SkillDocument skill) => ToMarkdown(skill.Name, skill.Description, skill.Instructions);

    private static string ToMarkdown(string name, string description, string instructions) =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{instructions.Trim()}\n";

    /// <summary>The skill as a .zip: SKILL.md at the root plus its resource files.</summary>
    public static byte[] ToZip(SkillDocument skill)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry($"{skill.Name}/{path}").Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add(ManifestName, ToMarkdown(skill));
            foreach (var f in skill.Files) Add(f.Path, f.Content);
        }

        return buffer.ToArray();
    }

    public static SkillDocument FromZip(Stream zip, SkillOptions? options = null)
    {
        var opts = options ?? new SkillOptions();
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new SkillPackageException("That isn't a valid .zip file.");
        }

        using (archive)
        {
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var manifest = entries.Where(e => e.Name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
                ?? throw new SkillPackageException($"The zip has no {ManifestName}.");
            // Everything is relative to the folder SKILL.md is in.
            var root = manifest.FullName[..^manifest.Name.Length];
            if (root.Count(c => c == '/') > 1) throw new SkillPackageException($"{ManifestName} must be at the top of the zip, or in one top-level folder.");

            var total = 0L;
            string? markdown = null;
            var files = new List<SkillFile>();
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(root, StringComparison.Ordinal)) continue;
                var path = NormalizePath(entry.FullName[root.Length..]);
                if (path is null || path.Split('/').Any(seg => seg.StartsWith('.'))) continue; // hidden files, __MACOSX etc.
                if (path.StartsWith("__MACOSX", StringComparison.Ordinal)) continue;

                total += entry.Length;
                if (total > opts.MaxTotalBytes) throw new SkillPackageException($"The skill is larger than {opts.MaxTotalBytes / 1024 / 1024} MB.");

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (!TryDecodeText(buffer.ToArray(), out var text)) continue; // binaries aren't kept

                if (entry == manifest) markdown = text;
                else files.Add(new SkillFile(path, text));
            }

            if (files.Count > opts.MaxFiles) throw new SkillPackageException($"A skill can have at most {opts.MaxFiles} files.");
            return Parse(markdown ?? throw new SkillPackageException($"{ManifestName} isn't readable text."), files.OrderBy(f => f.Path).ToList(), opts);
        }
    }

    private static SkillDocument Parse(string markdown, List<SkillFile> files, SkillOptions opts)
    {
        var m = Frontmatter().Match(markdown.TrimStart('﻿'));
        if (!m.Success) throw new SkillPackageException($"{ManifestName} must start with frontmatter: ---, name: …, description: …, ---.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in m.Groups[1].Value.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i <= 0 || char.IsWhiteSpace(line[0])) continue;
            fields[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"', '\'');
        }

        var name = fields.GetValueOrDefault("name") ?? throw new SkillPackageException("The frontmatter needs a name.");
        if (!NameRule().IsMatch(name)) throw new SkillPackageException("name must be 1–64 lowercase letters, digits and hyphens.");
        var description = fields.GetValueOrDefault("description");
        if (string.IsNullOrWhiteSpace(description)) throw new SkillPackageException("The frontmatter needs a description: say what the skill does and when to use it.");
        if (description.Length > 1024) throw new SkillPackageException("description must be at most 1,024 characters.");

        var body = markdown[(m.Index + m.Length)..].Trim();
        if (body.Length == 0) throw new SkillPackageException($"{ManifestName} has no instructions after the frontmatter.");
        if (body.Length > opts.MaxInstructionsChars) throw new SkillPackageException($"The instructions are longer than {opts.MaxInstructionsChars:N0} characters; move detail into resource files.");

        return new SkillDocument { Name = name, Description = description, Instructions = body, Files = files };
    }

    /// <summary>A safe relative path ("reference/api.md"), or null if it tries to escape.</summary>
    public static string? NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." || segment.Contains(':')) return null;
            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Contains((byte)0)) return false;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---[ \t]*(\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex NameRule();
}
