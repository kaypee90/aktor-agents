using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentRuntime.Evals;

/// <summary>Report output, and the regression check against a committed baseline.</summary>
public static class EvalReports
{
    public static void Write(EvalReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, EvalSpec.Json));
        File.WriteAllText(Path.Combine(directory, "report.md"), Markdown(report));
    }

    public static EvalReport Load(string path) =>
        JsonSerializer.Deserialize<EvalReport>(File.ReadAllText(path), EvalSpec.Json) ?? throw new InvalidOperationException($"Empty report: {path}");

    public static string Markdown(EvalReport report, IReadOnlyList<Regression>? regressions = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Eval report").AppendLine();
        sb.AppendLine($"Generated {report.GeneratedAt:u}; quality judged by `{report.Judge}` (0–5).").AppendLine();
        sb.AppendLine("| Scenario | Variant | Model | Prompt | Runs | Completed | Team (mean, max) | Depth (max) | Tokens (mean) | Cost $ (mean) | Duration s (p50 / p95) | Quality (mean) | Replay fidelity |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in report.Cells)
        {
            sb.Append($"| {c.Scenario} | {c.Variant} | {c.Model} | `{c.PromptVersion}` | {c.Runs} | {c.CompletionRate.ToString("P0", inv)} | ")
              .Append($"{c.TeamSize.Mean.ToString("0.#", inv)}, {c.TeamSize.Max.ToString("0", inv)} | {c.Depth.Max.ToString("0", inv)} | ")
              .Append($"{c.Tokens.Mean.ToString("N0", inv)} | {c.CostUsd.Mean.ToString("0.0000", inv)} | ")
              .Append($"{c.DurationSeconds.P50.ToString("0.0", inv)} / {c.DurationSeconds.P95.ToString("0.0", inv)} | {c.Quality.Mean.ToString("0.00", inv)} | ")
              .AppendLine(c.ReplayFidelity is { } f ? f.ToString("P0", inv) + " |" : "– |");
        }

        // Variants side by side, relative to the first variant of each scenario.
        foreach (var scenario in report.Cells.GroupBy(c => c.Scenario).Where(g => g.Count() > 1))
        {
            var first = scenario.First();
            sb.AppendLine().AppendLine($"## {scenario.Key}: compared with `{first.Variant}`").AppendLine();
            sb.AppendLine("| Variant | Completion | Team size | Cost | Quality |").AppendLine("|---|---|---|---|---|");
            foreach (var c in scenario.Skip(1))
            {
                sb.AppendLine($"| {c.Variant} | {Delta(c.CompletionRate, first.CompletionRate, true)} | {Delta(c.TeamSize.Mean, first.TeamSize.Mean)} | " +
                              $"{Delta(c.CostUsd.Mean, first.CostUsd.Mean)} | {Delta(c.Quality.Mean, first.Quality.Mean, absolute: true)} |");
            }
        }

        if (regressions is { Count: > 0 })
        {
            sb.AppendLine().AppendLine("## Regressions against the baseline").AppendLine();
            foreach (var r in regressions) sb.AppendLine($"- **{r.Scenario} / {r.Variant}**, {r.Metric}: {r.Message}");
        }

        return sb.ToString();
    }

    private static string Delta(double current, double baseline, bool absolute = false)
    {
        var inv = CultureInfo.InvariantCulture;
        if (absolute) return (current - baseline >= 0 ? "+" : "") + (current - baseline).ToString("0.##", inv);
        if (baseline == 0) return current == 0 ? "±0%" : "new";
        var pct = (current - baseline) / baseline;
        return (pct >= 0 ? "+" : "") + pct.ToString("P0", inv);
    }

    /// <summary>Tolerances for "this got worse": completion and quality may not drop, cost and team
    /// size may not grow, by more than these.</summary>
    public sealed record Tolerances(double CompletionDrop = 0.10, double QualityDrop = 0.5, double CostGrowth = 0.25, double TeamGrowth = 0.5, double FidelityDrop = 0.0);

    public static List<Regression> Compare(EvalReport baseline, EvalReport current, Tolerances? tolerances = null)
    {
        var t = tolerances ?? new Tolerances();
        var regressions = new List<Regression>();
        foreach (var b in baseline.Cells)
        {
            var c = current.Cells.FirstOrDefault(x => x.Scenario == b.Scenario && x.Variant == b.Variant);
            if (c is null)
            {
                regressions.Add(new Regression(b.Scenario, b.Variant, "missing", 0, 0, "is in the baseline but wasn't run"));
                continue;
            }

            void Check(string metric, double was, double now, bool worse, string message)
            {
                if (worse) regressions.Add(new Regression(b.Scenario, b.Variant, metric, was, now, message));
            }

            Check("completion_rate", b.CompletionRate, c.CompletionRate, c.CompletionRate < b.CompletionRate - t.CompletionDrop,
                $"completion fell from {b.CompletionRate:P0} to {c.CompletionRate:P0}");
            Check("quality", b.Quality.Mean, c.Quality.Mean, c.Quality.Mean < b.Quality.Mean - t.QualityDrop,
                $"quality fell from {b.Quality.Mean:0.00} to {c.Quality.Mean:0.00}");
            Check("cost_usd", b.CostUsd.Mean, c.CostUsd.Mean, b.CostUsd.Mean > 0 && c.CostUsd.Mean > b.CostUsd.Mean * (1 + t.CostGrowth),
                $"mean cost grew from ${b.CostUsd.Mean:0.0000} to ${c.CostUsd.Mean:0.0000}");
            Check("team_size", b.TeamSize.Mean, c.TeamSize.Mean, c.TeamSize.Mean > b.TeamSize.Mean * (1 + t.TeamGrowth),
                $"mean team size grew from {b.TeamSize.Mean:0.#} to {c.TeamSize.Mean:0.#}");
            if (b.ReplayFidelity is { } bf && c.ReplayFidelity is { } cf)
            {
                Check("replay_fidelity", bf, cf, cf < bf - t.FidelityDrop, $"replays reproduced {cf:P0} of runs, down from {bf:P0}");
            }
        }

        return regressions;
    }
}
