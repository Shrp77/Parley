using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Parley.Cli.Tests;

/// <summary>
/// A real <c>parley serve</c>, in its own process, for the tests that run the real binary.
/// </summary>
/// <remarks>
/// <para>
/// These tests existed before the transport and checked the operator's tool end to end against a
/// database file. They still do, and they now need two processes rather than one — which is not
/// an inconvenience the transport imposed on them but the thing they should have been checking
/// all along. A deployment is a broker and its clients, and a test that never started a broker
/// could not tell a working client from one that had quietly opened the file itself.
/// </para>
/// <para>
/// <b>The address is read from the broker's own output rather than chosen here.</b> Picking a
/// port means binding a socket to find a free one, closing it, and hoping nothing takes it in
/// between — which fails rarely, in parallel, and gets blamed on whatever test happened to be
/// running. Port zero lets the operating system choose and the broker says which it got.
/// </para>
/// </remarks>
internal sealed partial class BrokerProcess : IDisposable
{
    private readonly Process _process;

    private BrokerProcess(Process process, string address)
    {
        _process = process;
        Address = address;
    }

    /// <summary>Where this broker is listening.</summary>
    public string Address { get; }

    /// <summary>Starts a broker over a database file.</summary>
    /// <param name="databasePath">The store.</param>
    /// <param name="environment">Variables to launch it with.</param>
    /// <returns>The running broker.</returns>
    /// <exception cref="InvalidOperationException">It did not report an address.</exception>
    public static BrokerProcess Start(
        string databasePath, params (string Name, string Value)[] environment)
    {
        var process = ToolProcess.Launch(
            environment, "serve", "--db", databasePath, "--listen", "http://127.0.0.1:0");

        try
        {
            // The first line names the addresses it bound. Read rather than parsed loosely,
            // because a test that guessed the port would pass against any broker on the machine
            // — including one another test had left running.
            var announcement = process.StandardOutput.ReadLine();

            if (announcement is null || Listening().Match(announcement) is not { Success: true } found)
            {
                throw new InvalidOperationException(
                    $"The broker did not announce an address. It said: {announcement ?? "nothing"}");
            }

            return new BrokerProcess(process, found.Value);
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:\d+")]
    private static partial Regex Listening();

    /// <summary>Stops the broker.</summary>
    public void Dispose() => Stop(_process);

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone, which is the outcome this wanted.
        }

        process.Dispose();
    }
}
