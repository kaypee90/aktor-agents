using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRuntime.Studies;

/// <summary>
/// Turns a file (or a simulation's decisions) into a study dataset: the sandbox reads it, cleans
/// the column names, splits off the sealed holdout and profiles it; the split is kept under the
/// study's data folder and the dataset recorded. A file with the name of an existing dataset
/// becomes its next version.
/// </summary>
public static class StudyIngest
{
    public sealed record Request(string FileName, string LocalPath, double HoldoutFraction, string? TimeColumn,
        Dictionary<string, string>? Dictionary = null, string Kind = "uploaded", string? TableName = null);

    public sealed record Result(StudyDataset? Dataset, string? Error);

    /// <summary>"Tenant Renewals 2024.xlsx" → "tenant_renewals_2024".</summary>
    public static string TableNameFor(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var s = new string(stem.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        if (s.Length == 0) s = "dataset";
        if (char.IsDigit(s[0])) s = "t_" + s;
        return s.Length > 40 ? s[..40].TrimEnd('_') : s;
    }

    public static async Task<Result> IngestAsync(IStudyStore store, IAnalysisSandbox sandbox, StudyOptions options, StudyInfo study,
        Request request, CancellationToken ct)
    {
        var name = request.TableName ?? TableNameFor(request.FileName);
        var previous = await store.GetDatasetAsync(study.StudyId, name, ct);
        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        var job = new JsonObject
        {
            ["op"] = "ingest",
            ["file"] = $"raw/upload{extension}",
            ["holdout_fraction"] = Math.Clamp(request.HoldoutFraction, 0, 0.5),
            ["seed"] = 7
        };
        if (!string.IsNullOrWhiteSpace(request.TimeColumn)) job["time_column"] = request.TimeColumn;

        var outcome = await sandbox.RunAsync(new AnalysisJob
        {
            Job = job,
            Files = new Dictionary<string, string> { [$"raw/upload{extension}"] = request.LocalPath }
        }, ct);
        if (!outcome.Ok) return new Result(null, outcome.Error);
        if (!outcome.Files.TryGetValue("train.parquet", out var train)) return new Result(null, "The sandbox returned no data.");

        var profile = outcome.Result;
        profile.Remove("ok");
        var dataset = new StudyDataset
        {
            DatasetId = Guid.NewGuid().ToString("n")[..12],
            StudyId = study.StudyId,
            Name = name,
            FileName = request.FileName,
            Version = (previous?.Version ?? 0) + 1,
            Kind = request.Kind,
            Rows = profile["rows"]?.GetValue<long>() ?? 0,
            TrainRows = profile["train_rows"]?.GetValue<long>() ?? 0,
            HoldoutRows = profile["holdout_rows"]?.GetValue<long>() ?? 0,
            TimeColumn = profile["time_column"]?.GetValue<string>(),
            HoldoutFraction = request.HoldoutFraction,
            ProfileJson = profile.ToJsonString(),
            // A new version keeps the descriptions people wrote for columns that still exist.
            Dictionary = request.Dictionary ?? previous?.Dictionary ?? [],
            SizeBytes = new FileInfo(request.LocalPath).Length
        };

        var dir = StudyPaths.DatasetDir(options, study.StudyId, dataset.DatasetId);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "train.parquet"), train, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, "holdout.parquet"), outcome.Files.GetValueOrDefault("holdout.parquet") ?? [], ct);
        await store.AddDatasetAsync(dataset, ct);
        return new Result(dataset, null);
    }

    /// <summary>A simulation's decisions as a dataset (no holdout: it's the experiment's output).</summary>
    public static async Task<StudyDataset?> IngestRowsAsync(StudyToolSupport support, StudyInfo study, string tableName, string fileName,
        IReadOnlyList<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aktor-sim-{Guid.NewGuid():n}.jsonl");
        try
        {
            var sb = new StringBuilder();
            foreach (var row in rows) sb.AppendLine(JsonSerializer.Serialize(row));
            await File.WriteAllTextAsync(temp, sb.ToString(), ct);
            var result = await IngestAsync(support.Store, support.Sandbox, support.Options, study,
                new Request(fileName + ".jsonl", temp, 0, null, Kind: "simulated", TableName: tableName), ct);
            return result.Dataset;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
