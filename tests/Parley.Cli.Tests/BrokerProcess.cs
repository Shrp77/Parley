using Parley.Configuration;
using Parley.Core;
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

    /// <summary>What the project this harness serves is called.</summary>
    internal const string ProjectName = "probe";

    /// <summary>Where the store of the project this harness serves is, given a root.</summary>
    /// <param name="root">The throwaway root.</param>
    /// <returns>The store path, whether or not it exists yet.</returns>
    /// <remarks>
    /// Derived through <see cref="ProjectFolder"/> rather than composed here, so a test that opens the
    /// store afterwards opens the one the broker opened. Composing it would be a second answer to where
    /// a project's store is, which is exactly what 019 removed.
    /// </remarks>
    public static string StoreFor(string root) =>
        ProjectFolder.StoreFor(root, Parley.Core.ProjectName.Create(ProjectName));

    /// <summary>Starts a broker over a throwaway root, serving one project in it.</summary>
    /// <param name="root">Where this rig keeps its Parley files. Created if absent.</param>
    /// <param name="environment">Variables to launch it with.</param>
    /// <returns>The running broker.</returns>
    /// <exception cref="InvalidOperationException">It did not report an address.</exception>
    /// <remarks>
    /// <para>
    /// <b>This took a store path and a listen address until 019, and both options are gone.</b> A
    /// project's store is derived from its name and its port is claimed when it is created, so a rig
    /// names a root and writes a project into it.
    /// </para>
    /// <para>
    /// <b>The project's endpoint is port zero, and that is what keeps the suite parallel.</b> Claiming a
    /// real port would put every test class on the same band from the bottom — `PortLedger` reconciles
    /// within one root, and a rig gives every class a root of its own, so two classes would both be
    /// handed 19100 and one of them would fail to listen. Port zero lets the operating system choose and
    /// the broker says which it got, which is the mechanism this harness already rested on.
    /// </para>
    /// </remarks>
    public static BrokerProcess Start(
        string root, params (string Name, string Value)[] environment)
    {
        Directory.CreateDirectory(root);

        ProjectManifest.Write(
            root,
            [new ProjectSettings(
                Parley.Core.ProjectName.Create(ProjectName),
                StoreFor(root),
                "http://127.0.0.1:0",
                null)]);

        var process = ToolProcess.Launch(environment, "serve", "--config", root);

        try
        {
            // Read rather than parsed loosely, because a test that guessed the port would pass
            // against any broker on the machine — including one another test had left running.
            //
            // Scanned rather than taken from line one, and the change is not cosmetic: this used
            // to read exactly the first line, so the day the broker gained a line naming its own
            // version every test that starts one failed at once, none of them for a reason to do
            // with what they were testing. What the banner promises is that an address appears,
            // not that it appears first.
            var said = new List<string>();
            string? announcement;

            while ((announcement = process.StandardOutput.ReadLine()) is not null)
            {
                said.Add(announcement);

                if (Listening().Match(announcement) is { Success: true } found)
                {
                    return new BrokerProcess(process, found.Value);
                }
            }

            throw new InvalidOperationException(
                "The broker did not announce an address. It said: " +
                (said.Count == 0 ? "nothing" : string.Join(" / ", said)));
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
