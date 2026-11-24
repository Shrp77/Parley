using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node ending a process a node that is gone left running ends what that process started too (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// An agent's process runs its own shells and tests. Ended alone, they run on with nobody holding them while the person is
/// told the process was ended. Review, lens 3 on 6257cf80: <c>entireProcessTree: false</c> passed every test.
/// </remarks>
[Integration]
public sealed class AKillEndsTheWholeTreeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("parley-node-kill-tree").FullName;

    public void Dispose()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);

                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return;
                }

                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public async Task What_the_left_process_started_is_ended_with_it()
    {
        var said = Path.Combine(_folder, "child.pid");
        using var parent = StartParent(said);
        Process? child = null;

        try
        {
            child = await ChildAsync(said, parent);

            var output = new StringWriter();

            await NodeCommand.KillAsync(
                new Wire.KillProcess { Agent = "docs@alpha", ProcessId = parent.Id, Because = "stuck" },
                new ConcurrentDictionary<AgentIdentity, ResidentAgent>(),
                output);

            Assert.Contains($"ended process {parent.Id}", output.ToString(), StringComparison.Ordinal);
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "what the process started is still running");
        }
        finally
        {
            End(child);
            End(parent);
            child?.Dispose();
        }
    }

    // A copy of the shell named for the agent's own kind, which starts a child that writes its own number and sleeps.
    private Process StartParent(string said)
    {
        var copy = Path.Combine(_folder, "claude" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        ProcessStartInfo started;

        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), copy);
            started = new ProcessStartInfo(copy)
            {
                Arguments = "/d /c powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '"
                    + said + "' -Value $PID; Start-Sleep -Seconds 120\"",
            };
        }
        else
        {
            File.Copy("/bin/sh", copy);

            var loop = Path.Combine(_folder, "tree.sh");

            File.WriteAllLines(loop, ["sleep 120 &", $"echo $! > '{said}'", "wait"]);
            started = new ProcessStartInfo(copy);
            started.ArgumentList.Add(loop);
        }

        started.UseShellExecute = false;
        started.CreateNoWindow = true;

        return Process.Start(started) ?? throw new InvalidOperationException("The stand-in did not start.");
    }

    private static async Task<Process> ChildAsync(string said, Process parent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(said)
                && int.TryParse((await ReadAsync(said)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return Process.GetProcessById(id);
            }

            Assert.False(parent.HasExited, "the stand-in ended before its child said its number");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The stand-in's child never said its number.");
    }

    private static async Task<string> ReadAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void End(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
            // It had already gone.
        }
    }
}
