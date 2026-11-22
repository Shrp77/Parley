using System.Collections.Concurrent;
using System.Diagnostics;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// What a node does when a person ends a process a node that is gone left running (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// The process is a label an adapter gave, so what is held here is what the node checks before it ends anything: only a
/// running process named <c>claude</c>, never this node, never one it hosts, never a number that names nothing. The stand-in
/// is a copy of an ordinary program that sleeps, given the agent's name.
/// </remarks>
[Integration]
public sealed class NodeKillTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("parley-node-kill").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts a copy of a program that stays up, under a name of its own, and returns the process.</summary>
    /// <remarks>
    /// Off Windows the copy is of the shell, told to loop. A copy of <c>sleep</c> used to do: where the system's tools are one
    /// program that looks at the name it was run under, a copy under any other name exits at once saying it knows no such
    /// program, and these tests then passed or failed by whether they looked before it had gone.
    /// </remarks>
    private Process Sleeper(string name)
    {
        var source = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "ping.exe")
            : "/bin/sh";

        if (!File.Exists(source))
        {
            Assert.Skip($"{source} is not here, so there is no stand-in to run.");
        }

        var copy = Path.Combine(_folder, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

        File.Copy(source, copy);

        var started = new ProcessStartInfo(copy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        if (OperatingSystem.IsWindows())
        {
            started.ArgumentList.Add("-n");
            started.ArgumentList.Add("120");
            started.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            // The loop is a file, and its path the one argument, so a test that makes a command line of these still can.
            var loop = Path.Combine(_folder, name + "-loop.sh");

            File.WriteAllLines(loop, ["n=0", "while [ $n -lt 120 ]; do sleep 1; n=$((n+1)); done"]);
            started.ArgumentList.Add(loop);
        }

        return Process.Start(started) ?? throw new InvalidOperationException("The stand-in did not start.");
    }

    private static async Task<string> KillAsync(long process, ConcurrentDictionary<AgentIdentity, ResidentAgent>? residents = null)
    {
        var said = new StringWriter();

        await NodeCommand.KillAsync(
            new Wire.KillProcess { Agent = "docs@alpha", ProcessId = process, Because = "stuck" },
            residents ?? new ConcurrentDictionary<AgentIdentity, ResidentAgent>(),
            said);

        return said.ToString();
    }

    [Fact]
    public async Task A_process_named_for_the_agents_own_kind_is_ended()
    {
        using var left = Sleeper("claude");

        var said = await KillAsync(left.Id);

        Assert.Contains($"ended process {left.Id}", said, StringComparison.Ordinal);
        Assert.True(left.WaitForExit(TimeSpan.FromSeconds(10)), "it is still running");
    }

    [Fact]
    public async Task A_process_of_any_other_kind_is_left_alone_however_the_adapter_named_it()
    {
        using var other = Sleeper("somebodys-program");

        try
        {
            var said = await KillAsync(other.Id);

            Assert.Contains("not an agent's own kind of process", said, StringComparison.Ordinal);
            Assert.False(other.HasExited, "it was ended");
        }
        finally
        {
            other.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_node_never_ends_itself()
    {
        var said = await KillAsync(Environment.ProcessId);

        Assert.Contains("this node", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_the_node_hosts_is_stopped_and_not_killed()
    {
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        // A hosted agent that is named claude, so only the hosting check can refuse it.
        using var hosted = Sleeper("claude");
        var resident = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse($"\"{hosted.StartInfo.FileName}\" {string.Join(' ', hosted.StartInfo.ArgumentList)}"),
            _folder, TimeProvider.System);

        residents[AgentIdentity.Mint()] = resident;

        try
        {
            var said = await KillAsync(resident.ProcessId, residents);

            Assert.Contains("one this node hosts", said, StringComparison.Ordinal);
            Assert.True(resident.IsAlive, "it was ended");
        }
        finally
        {
            await resident.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public async Task A_number_that_cannot_be_a_process_ends_nothing(long process)
    {
        var said = await KillAsync(process);

        Assert.Contains("named no process that can be one", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_that_has_already_gone_is_said_not_to_be_running()
    {
        using var gone = Sleeper("claude");
        var id = gone.Id;

        gone.Kill(entireProcessTree: true);
        gone.WaitForExit();

        var said = await KillAsync(id);

        Assert.Contains($"process {id} is not running", said, StringComparison.Ordinal);
    }
}
