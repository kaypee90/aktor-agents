namespace AgentRuntime.Evals;

/// <summary>
/// aktor-eval: runs goals N times through the real runtime and reports team size and depth, cost,
/// duration, completion rate and judged output quality, per model / prompt / config variant.
///
/// <code>
/// dotnet run --project src/AgentRuntime.Evals -- --spec evals/demo-scenarios.json --out evals/reports/latest \
///     [--baseline evals/baseline/report.json] [--set Llm:Provider=Anthropic --set Llm:Model=…] [--runs 5]
/// </code>
/// Exit code 1 when a baseline is given and a metric regressed beyond its tolerance.
/// </summary>
public static class EvalCli
{
    public static async Task<int> Main(string[] args)
    {
        string? spec = null, outDir = null, baseline = null;
        // The server's own defaults (budgets, limits), so evals measure what users get.
        var config = File.Exists("src/AgentRuntime.Api/appsettings.json") ? "src/AgentRuntime.Api/appsettings.json" : null;
        int? runs = null;
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--spec": spec = Next(); break;
                case "--out": outDir = Next(); break;
                case "--baseline": baseline = Next(); break;
                case "--runs": runs = int.Parse(Next()); break;
                case "--config": config = Next(); break;
                case "--set":
                    var kv = Next().Split('=', 2);
                    settings[kv[0]] = kv.Length > 1 ? kv[1] : null;
                    break;
                case "-h" or "--help":
                    Console.WriteLine("aktor-eval --spec <spec.json> --out <dir> [--baseline <report.json>] [--runs N] [--config appsettings.json] [--set Key=Value]...");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}");
                    return 2;
            }
        }

        if (spec is null || outDir is null)
        {
            Console.Error.WriteLine("aktor-eval --spec <spec.json> --out <dir> [--baseline <report.json>] [--runs N] [--set Key=Value]...");
            return 2;
        }

        // API keys come from the environment (e.g. Llm__ApiKey), never from the spec file.
        foreach (var key in new[] { "Llm__Provider", "Llm__Model", "Llm__ApiKey", "Llm__BaseUrl", "Llm__FastModel" })
        {
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) settings.TryAdd(key.Replace("__", ":"), value);
        }

        var evalSpec = EvalSpec.Load(spec);
        if (runs is { } n) evalSpec = evalSpec with { Scenarios = evalSpec.Scenarios.Select(s => s with { Runs = n }).ToList() };

        if (config is not null)
        {
            Console.WriteLine($"Runtime configuration: {config} (overridden by --set and the spec's variants)");
            foreach (var (k, v) in EvalRunner.LoadSettings(config)) settings.TryAdd(k, v);
        }

        var report = await new EvalRunner(Console.WriteLine).RunAsync(evalSpec, settings);
        var regressions = baseline is null ? [] : EvalReports.Compare(EvalReports.Load(baseline), report);
        EvalReports.Write(report, outDir);
        if (regressions.Count > 0) File.WriteAllText(Path.Combine(outDir, "report.md"), EvalReports.Markdown(report, regressions));

        Console.WriteLine();
        Console.WriteLine(EvalReports.Markdown(report, regressions));
        if (regressions.Count > 0)
        {
            Console.Error.WriteLine($"{regressions.Count} regression(s) against {baseline}.");
            return 1;
        }

        return 0;
    }
}
