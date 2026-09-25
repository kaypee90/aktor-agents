using System.Diagnostics;
using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Tools;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Runs a shell command inside an isolated, resource-limited, network-disabled Docker container
/// (CLAUDE.md section 19) rather than on the host. The agent workspace is bind-mounted so file
/// artifacts survive the container's lifetime.
/// </summary>
public sealed class ShellExecTool(IOptions<ToolsOptions> options) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "shell_exec",
        SideEffects = ToolSideEffects.NonIdempotent,
        Description = "Run a shell command in an isolated, network-disabled Docker container with your " +
                      "task workspace mounted at /workspace. Use for builds, scripts, or data processing.",
        RequiredPermissions = ToolPermission.ExecuteShell,
        JsonSchema = """{ "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<ShellArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid shell_exec arguments.");

        var opts = options.Value;
        var workspace = WorkspacePath.TaskRoot(opts, request.TaskId);

        // Named so a timeout can remove the container itself: killing the `docker run` client
        // process does not stop the container, which would keep running past the time limit.
        var containerName = $"agent-shell-{Guid.NewGuid():n}";

        var dockerArgs = new List<string>
        {
            "run", "--rm",
            "--name", containerName,
            "--network", "none",
            "--memory", opts.ShellMemoryLimit,
            "--cpus", opts.ShellCpuLimit,
            "-v", $"{workspace}:/workspace",
            "-w", "/workspace",
            opts.ShellDockerImage,
            "sh", "-c", args.Command
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.ShellTimeoutSeconds));

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in dockerArgs) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                exitCode = process.ExitCode,
                stdout = Truncate(stdout),
                stderr = Truncate(stderr)
            }, ToolJson.Options));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await TryRemoveContainerAsync(containerName);
            return ToolExecutionResult.Fail($"shell_exec timed out after {opts.ShellTimeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to run sandboxed shell command: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static async Task TryRemoveContainerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
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

    private static string Truncate(string s) => s.Length > 10_000 ? s[..10_000] + "...(truncated)" : s;

    private sealed record ShellArgs(string Command);
}
