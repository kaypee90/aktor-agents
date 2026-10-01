using System.Net;
using AgentRuntime.Infrastructure.Tools;

namespace AgentRuntime.Tests;

/// <summary>
/// Sandboxed execution by default (docs/guarantees.md): agent-generated commands never run on the
/// host, files stay inside the task's own workspace, and agent HTTP can't reach private networks.
/// </summary>
public sealed class SandboxGuaranteeTests
{
    private static readonly ToolsOptions Options = new()
    {
        WorkspaceRoot = Path.Combine(Path.GetTempPath(), "aktor-sandbox-tests"),
        ShellDockerImage = "alpine:3.20",
        ShellMemoryLimit = "256m",
        ShellCpuLimit = "0.5"
    };

    [Fact]
    public void Shell_commands_run_in_a_container_with_no_network_capped_resources_and_only_the_task_workspace()
    {
        var workspace = WorkspacePath.TaskRoot(Options, "task-1");
        var args = ShellExecTool.DockerArguments(Options, workspace, "agent-shell-x", "rm -rf / ; curl evil.example");

        Assert.Equal("run", args[0]);
        Assert.Equal("none", args[args.IndexOf("--network") + 1]);
        Assert.Equal("256m", args[args.IndexOf("--memory") + 1]);
        Assert.Equal("0.5", args[args.IndexOf("--cpus") + 1]);
        Assert.Single(args, a => a == "-v");
        Assert.Equal($"{workspace}:/workspace", args[args.IndexOf("-v") + 1]);
        Assert.Contains("--rm", args);
        Assert.DoesNotContain("--privileged", args);
        // The command is one argument to sh inside the container, never interpreted by a host shell.
        Assert.Equal(["sh", "-c", "rm -rf / ; curl evil.example"], args[^3..]);
    }

    [Theory]
    [InlineData("../task-2/secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("notes/../../task-1x/leak.md")]
    public void Paths_outside_the_task_workspace_are_refused(string path) =>
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePath.Resolve(Options, "task-1", path));

    [Theory]
    [InlineData("report.md")]
    [InlineData("/report.md")]
    [InlineData("src/../report.md")]
    public void Paths_inside_the_task_workspace_resolve_under_it(string path) =>
        Assert.StartsWith(WorkspacePath.TaskRoot(Options, "task-1"), WorkspacePath.Resolve(Options, "task-1", path));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.17.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Agent_http_reaches_public_addresses_only(string address, bool allowed) =>
        Assert.Equal(allowed, PublicNetworkHandler.IsPublic(IPAddress.Parse(address)));
}
