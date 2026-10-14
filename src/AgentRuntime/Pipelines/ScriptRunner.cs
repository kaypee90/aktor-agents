namespace AgentRuntime.Pipelines;

/// <summary>Where and how script stages run (configuration section "Scripts").</summary>
public sealed class ScriptOptions
{
    public const string SectionName = "Scripts";

    /// <summary>Image for Python scripts. The study analysis image (aktor-analysis:1) also works
    /// and adds pandas, numpy and friends.</summary>
    public string PythonImage { get; set; } = "python:3.12-slim";
    public string NodeImage { get; set; } = "node:22-slim";
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxTimeoutSeconds { get; set; } = 300;
    public string MemoryLimit { get; set; } = "256m";
    public string CpuLimit { get; set; } = "0.5";
    /// <summary>Most characters of a script's printed output kept as its logs.</summary>
    public int MaxLogChars { get; set; } = 10_000;
}

/// <summary>One script to run: its code and the data it's given.</summary>
public sealed record ScriptRunRequest
{
    public required string Language { get; init; }
    public required string Code { get; init; }
    /// <summary>The JSON the script receives as <c>data</c>.</summary>
    public required string DataJson { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record ScriptRunResult
{
    public bool Ok { get; init; }
    /// <summary>What the script returned, as JSON (null when it returned nothing).</summary>
    public string OutputJson { get; init; } = "null";
    public string? Error { get; init; }
    /// <summary>What it printed.</summary>
    public string Logs { get; init; } = string.Empty;
    public long DurationMs { get; init; }

    public static ScriptRunResult Failed(string error, string logs = "", long ms = 0) => new() { Ok = false, Error = error, Logs = logs, DurationMs = ms };
}

/// <summary>
/// Runs a script stage's code in isolation (CLAUDE.md section 19): no network, capped memory,
/// CPU, processes and time, and nothing from the host mounted. The code is the body of a function
/// given <c>data</c>; what it returns is the result.
/// </summary>
public interface IScriptRunner
{
    Task<ScriptRunResult> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The default when the host provides no sandbox: scripts are refused, never run on the host.</summary>
public sealed class UnavailableScriptRunner : IScriptRunner
{
    public Task<ScriptRunResult> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScriptRunResult.Failed("Script stages need the Docker sandbox, which this server doesn't have."));
}
