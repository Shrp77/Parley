using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Several servers launched against one project at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// The reboot the deployment of §1 implies: every agent has its own process, so a machine coming
/// back starts all of them at once against a database only one of them can create. Two of them
/// read an empty schema version, both decide to apply the first migration, SQLite serialises
/// them, and the loser fails with "table agents already exists" and exits — so what somebody
/// sees is one repository that opened and one that would not, differently each time.
/// </para>
/// <para>
/// <b>Real processes, and that is not incidental.</b> The same race written with threads and
/// several connections inside one process does not reproduce it: four hundred attempts against
/// the unfixed migrator all passed, while twelve pairs of real processes failed two of them.
/// Whatever serialises connections within a process hides exactly the interleaving that matters,
/// so an in-process version of this test would have been a check that passed while testing
/// nothing — and would have been written first, because it is the easier one to write.
/// </para>
/// <para>
/// The server is spawned from the test's own output directory, which the project reference to
/// Parley.Mcp populates, so this needs nothing installed and runs the same way on a CI agent.
/// </para>
/// </remarks>
public sealed class ConcurrentLaunchTests : IDisposable
{
    // Six at a time, ten times over, because the window is narrow and a test that catches a
    // race only sometimes reports green on a broken migrator. One round of six caught the
    // unfixed version about a third of the time; ten independent rounds is what turns that into
    // a check worth having. The whole thing still runs in a few seconds, because a server that
    // starts is stopped immediately rather than waited on.
    private const int Servers = 6;
    private const int Rounds = 10;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-launch-" + Guid.NewGuid().ToString("N"));

    public ConcurrentLaunchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Under the temporary path and named for a GUID. Leaving it is untidy; failing a
            // passing test over a handle the operating system has not released would be worse.
        }
    }

    /// <summary>
    /// Finds the server binary beside the tests, and how to run it.
    /// </summary>
    /// <returns>The executable to launch and the argument to pass it, if any.</returns>
    /// <remarks>
    /// The native apphost when there is one, and <c>dotnet parley-mcp.dll</c> otherwise. The
    /// fallback is not hypothetical tidiness: the apphost is platform-specific and a build that
    /// suppresses it would turn this into a red test on one CI platform and green on the other
    /// two, which reads as a product defect and is not one.
    /// </remarks>
    private static (string FileName, string? Argument) Server()
    {
        var directory = Path.GetDirectoryName(typeof(ConcurrentLaunchTests).Assembly.Location)!;
        var apphost = Path.Combine(
            directory, OperatingSystem.IsWindows() ? "parley-mcp.exe" : "parley-mcp");

        if (File.Exists(apphost))
        {
            return (apphost, null);
        }

        var library = Path.Combine(directory, "parley-mcp.dll");

        Assert.True(File.Exists(library), $"expected the server beside the tests in {directory}");

        return ("dotnet", library);
    }

    private static Process Launch(
        (string FileName, string? Argument) server, string databasePath, string agent)
    {
        var start = new ProcessStartInfo(server.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (server.Argument is { } argument)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[ServerOptions.DatabaseVariable] = databasePath;
        start.Environment[ServerOptions.AgentVariable] = agent;

        return Process.Start(start)!;
    }

    [Fact]
    public void Every_server_starting_at_once_against_a_new_project_comes_up()
    {
        var server = Server();

        for (var round = 0; round < Rounds; round++)
        {
            var databasePath = Path.Combine(_directory, $"round-{round}", "parley.db");
            var processes = new List<(string Agent, Process Process)>();

            try
            {
                for (var i = 0; i < Servers; i++)
                {
                    processes.Add(($"repo-{i}", Launch(server, databasePath, $"repo-{i}")));
                }

                // Stdin is closed rather than the process being waited on, because a server
                // that got past start-up serves until its client closes the pipe and would
                // otherwise be waited on until the timeout. Closing it is what a client does on
                // shutdown, and it turns "still healthy" into an exit code: a server that came
                // up exits 0, and one that could not exits 74 having said why on stderr.
                foreach (var (_, process) in processes)
                {
                    process.StandardInput.Close();
                }

                var died = new List<string>();

                foreach (var (agent, process) in processes)
                {
                    Assert.True(
                        process.WaitForExit(TimeSpan.FromSeconds(30)),
                        $"{agent} never exited after its input closed");

                    if (process.ExitCode != 0)
                    {
                        died.Add($"{agent} exited {process.ExitCode}: " +
                                 process.StandardError.ReadToEnd().Trim());
                    }
                }

                Assert.True(
                    died.Count == 0,
                    $"round {round}: {died.Count} of {Servers} servers failed to start. " +
                    string.Join(" | ", died));
            }
            finally
            {
                foreach (var (_, process) in processes)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    process.WaitForExit(TimeSpan.FromSeconds(10));
                    process.Dispose();
                }
            }
        }
    }
}
