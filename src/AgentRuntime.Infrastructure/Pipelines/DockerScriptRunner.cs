using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Pipelines;

/// <summary>
/// Runs a script stage in a throwaway container from a stock Python or Node image: the code and its
/// data go in on stdin, the result comes back on stdout after a marker. Nothing on the host is
/// mounted, so this works the same when the API itself runs in a container next to the Docker
/// daemon. No network, read-only root, a small /tmp, no capabilities, an unprivileged user, and
/// memory, CPU, process and time limits (CLAUDE.md section 19).
/// </summary>
public sealed class DockerScriptRunner(IOptions<ScriptOptions> options, ILogger<DockerScriptRunner> logger) : IScriptRunner
{
    private const string ResultMarker = "__AKTOR_RESULT__";

    /// <summary>The user's code becomes the body of <c>main(data)</c>; prints are captured as logs.</summary>
    private const string PythonWrapper = """
        import sys, json, io, contextlib, textwrap, traceback
        job = json.load(sys.stdin)
        out = sys.stdout
        buf = io.StringIO()
        src = "def __aktor_main(data):\n" + textwrap.indent(job["code"], "    ") + "\n    return None\n"
        def where(tb):
            frames = [f for f in traceback.extract_tb(tb) if f.filename == "<script>"]
            return f" (line {frames[-1].lineno - 1})" if frames else ""
        try:
            ns = {}
            with contextlib.redirect_stdout(buf):
                exec(compile(src, "<script>", "exec"), ns)
                value = ns["__aktor_main"](job["data"])
            try:
                encoded = json.dumps(value, allow_nan=False)
                result = {"ok": True, "output": json.loads(encoded)}
            except (TypeError, ValueError) as e:
                result = {"ok": False, "error": f"The script returned something that isn't JSON: {e}"}
        except SyntaxError as e:
            result = {"ok": False, "error": f"SyntaxError on line {(e.lineno or 1) - 1}: {e.msg}"}
        except BaseException as e:
            result = {"ok": False, "error": f"{type(e).__name__}{where(e.__traceback__)}: {e}"}
        result["logs"] = buf.getvalue()[-20000:]
        out.write("\n__AKTOR_RESULT__" + json.dumps(result))
        """;

    /// <summary>The user's code becomes the body of an async function of <c>data</c>.</summary>
    private const string NodeWrapper = """
        const chunks = [];
        process.stdin.on("data", (c) => chunks.push(c));
        process.stdin.on("end", async () => {
          const job = JSON.parse(Buffer.concat(chunks).toString("utf8"));
          const logs = [];
          const show = (x) => { if (typeof x === "string") return x; try { return JSON.stringify(x); } catch { return String(x); } };
          console.log = console.info = console.warn = console.error = console.debug = (...a) => logs.push(a.map(show).join(" "));
          let result;
          try {
            const fn = new Function("data", "\"use strict\"; return (async () => {\n" + job.code + "\n})();");
            const value = await fn(job.data);
            const encoded = JSON.stringify(value === undefined ? null : value);
            result = { ok: true, output: encoded === undefined ? null : JSON.parse(encoded) };
          } catch (e) {
            result = { ok: false, error: e && e.name ? `${e.name}: ${e.message}` : String(e) };
          }
          result.logs = logs.join("\n").slice(-20000);
          process.stdout.write("\n__AKTOR_RESULT__" + JSON.stringify(result));
        });
        """;

    internal static List<string> DockerArguments(ScriptOptions o, string containerName, string language)
    {
        var python = language == ScriptLanguages.Python;
        return
        [
            "run", "--rm", "-i",
            "--name", containerName,
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,size=64m",
            "--memory", o.MemoryLimit,
            "--cpus", o.CpuLimit,
            "--pids-limit", "64",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "65534:65534",
            "-e", "HOME=/tmp",
            "--entrypoint", python ? "python" : "node",
            python ? o.PythonImage : o.NodeImage,
            python ? "-c" : "-e",
            python ? PythonWrapper : NodeWrapper
        ];
    }

    public async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!ScriptLanguages.IsKnown(request.Language)) return ScriptRunResult.Failed($"Unknown language '{request.Language}'.");

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, o.MaxTimeoutSeconds);
        var containerName = $"aktor-script-{Guid.NewGuid():n}";
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in DockerArguments(o, containerName, request.Language)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            JsonNode? data;
            try
            {
                data = JsonNode.Parse(request.DataJson);
            }
            catch (JsonException)
            {
                data = JsonValue.Create(request.DataJson);
            }

            var job = new JsonObject { ["code"] = request.Code, ["data"] = data };
            await process.StandardInput.WriteAsync(job.ToJsonString().AsMemory(), timeout.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return Parse(stdout, stderr, process.ExitCode, o, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await RemoveContainerAsync(containerName);
            return ScriptRunResult.Failed($"The script took longer than {timeoutSeconds}s and was stopped.", ms: watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await RemoveContainerAsync(containerName);
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "Couldn't run the script sandbox");
            Kill(process);
            await RemoveContainerAsync(containerName);
            return ScriptRunResult.Failed($"The script sandbox isn't available ({ex.Message}). Is Docker running?", ms: watch.ElapsedMilliseconds);
        }
    }

    private ScriptRunResult Parse(string stdout, string stderr, int exitCode, ScriptOptions o, long ms)
    {
        var at = stdout.LastIndexOf(ResultMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            var reason = exitCode == 137 ? "it ran out of memory"
                : stderr.Contains("Unable to find image", StringComparison.Ordinal) || stderr.Contains("pull access denied", StringComparison.Ordinal)
                    ? "its image couldn't be found or downloaded"
                : Last(stderr.Trim(), 600);
            logger.LogInformation("Script sandbox ended without a result (exit {Exit}): {Stderr}", exitCode, Last(stderr, 2000));
            return ScriptRunResult.Failed($"The script sandbox failed: {(reason.Length == 0 ? $"exit code {exitCode}" : reason)}", Last(stdout, o.MaxLogChars), ms);
        }

        JsonObject? result;
        try
        {
            result = JsonNode.Parse(stdout[(at + ResultMarker.Length)..]) as JsonObject;
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result is null) return ScriptRunResult.Failed("The script sandbox returned an unreadable result.", ms: ms);
        var logs = Last((stdout[..at].Trim() + "\n" + (result["logs"]?.GetValue<string>() ?? "")).Trim(), o.MaxLogChars);
        return result["ok"]?.GetValue<bool>() == true
            ? new ScriptRunResult { Ok = true, OutputJson = result["output"]?.ToJsonString() ?? "null", Logs = logs, DurationMs = ms }
            : ScriptRunResult.Failed(result["error"]?.GetValue<string>() ?? "The script failed.", logs, ms);
    }

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
