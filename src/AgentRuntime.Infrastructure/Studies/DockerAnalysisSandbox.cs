using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using System.Text.Json.Nodes;
using AgentRuntime.Studies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Studies;

/// <summary>
/// Runs one analysis job in the analysis image (docker/analysis): the job and its files go in as a
/// tar stream on stdin, the result and the files it wrote come back as a tar stream on stdout.
/// Nothing on the host is mounted, so this works the same when the API itself runs in a container
/// next to the Docker daemon. No network, read-only root, a size-limited /tmp, no capabilities, and
/// memory, CPU, process and time limits (CLAUDE.md section 19).
/// </summary>
public sealed class DockerAnalysisSandbox(IOptions<StudyOptions> options, ILogger<DockerAnalysisSandbox> logger) : IAnalysisSandbox
{
    internal static List<string> DockerArguments(StudyOptions o, string containerName) =>
    [
        "run", "--rm", "-i",
        "--name", containerName,
        "--network", "none",
        "--read-only",
        "--tmpfs", "/tmp:rw,size=1g",
        "--memory", o.AnalysisMemoryLimit,
        "--cpus", o.AnalysisCpuLimit,
        "--pids-limit", "256",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        o.AnalysisImage
    ];

    public async Task<AnalysisOutcome> RunAsync(AnalysisJob job, CancellationToken ct = default)
    {
        var o = options.Value;
        var containerName = $"aktor-analysis-{Guid.NewGuid():n}";
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.AnalysisTimeoutSeconds));

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in DockerArguments(o, containerName)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);

            await using (var stdin = process.StandardInput.BaseStream)
            {
                await WriteJobAsync(stdin, job, timeout.Token);
            }

            await process.WaitForExitAsync(timeout.Token);
            await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || output.Length == 0)
            {
                var reason = process.ExitCode == 137 ? "it ran out of memory" : Last(stderr, 600);
                logger.LogWarning("Analysis job failed (exit {Exit}): {Stderr}", process.ExitCode, Last(stderr, 2000));
                return Failed($"The analysis sandbox failed: {reason}", watch.ElapsedMilliseconds);
            }

            output.Position = 0;
            return await ReadResultAsync(output, watch.ElapsedMilliseconds, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            await RemoveContainerAsync(containerName);
            return Failed($"The analysis took longer than {o.AnalysisTimeoutSeconds}s and was stopped. Work on a sample or simplify it.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "Couldn't run the analysis sandbox");
            Kill(process);
            await RemoveContainerAsync(containerName);
            return Failed($"The analysis sandbox isn't available ({ex.Message}). Is Docker running and the '{o.AnalysisImage}' image built (docker/analysis)?",
                watch.ElapsedMilliseconds);
        }
    }

    private static async Task WriteJobAsync(Stream stdin, AnalysisJob job, CancellationToken ct)
    {
        await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
        var jobBytes = Encoding.UTF8.GetBytes(job.Job.ToJsonString());
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "job.json") { DataStream = new MemoryStream(jobBytes) }, ct);
        foreach (var (name, path) in job.Files)
        {
            await using var file = File.OpenRead(path);
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = file }, ct);
        }
    }

    private static async Task<AnalysisOutcome> ReadResultAsync(Stream output, long elapsedMs, CancellationToken ct)
    {
        JsonObject? result = null;
        var files = new Dictionary<string, byte[]>();
        await using var tar = new TarReader(output);
        while (await tar.GetNextEntryAsync(copyData: true, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
            using var data = new MemoryStream();
            await entry.DataStream.CopyToAsync(data, ct);
            var name = Path.GetFileName(entry.Name);
            if (name == "result.json") result = JsonNode.Parse(data.ToArray()) as JsonObject;
            else files[name] = data.ToArray();
        }

        if (result is null) return Failed("The analysis sandbox returned no result.", elapsedMs);
        var ok = result["ok"]?.GetValue<bool>() == true;
        return new AnalysisOutcome
        {
            Ok = ok,
            Result = result,
            Error = ok ? null : result["error"]?.GetValue<string>() ?? "The analysis failed.",
            Files = files,
            DurationMs = elapsedMs
        };
    }

    private static AnalysisOutcome Failed(string error, long elapsedMs) =>
        new() { Ok = false, Result = new JsonObject { ["ok"] = false, ["error"] = error }, Error = error, DurationMs = elapsedMs };

    private static string Last(string s, int n) => s.Length > n ? s[^n..] : s;

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static async Task RemoveContainerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("rm");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(containerName);
            using var rm = Process.Start(psi);
            if (rm is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await rm.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // best effort
        }
    }
}
