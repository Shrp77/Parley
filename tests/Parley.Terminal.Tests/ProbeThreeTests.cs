using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The third run of each lens over the catalogue reader's move.
/// </summary>
/// <remarks>
/// The earlier runs asked what the reader answers and what it does under a writer. These ask what
/// it leaves behind: handles held by a path read on every draw, the launch command in a line the
/// node prints to a log, and whether the two project files say what the move claims they say.
/// </remarks>
public sealed class ProbeThreeTests
{
    /// <summary>Lens 1: reading on every draw leaves nothing held.</summary>
    /// <remarks>
    /// <para>
    /// <c>Shell.Catalogue</c> re-reads the file for each render, deliberately, so a leaked handle
    /// is not a slow leak here — it is one per frame. Windows makes the consequence visible: a
    /// held handle refuses the delete, which is what an operator editing the file would hit.
    /// </para>
    /// <para>
    /// <see cref="NodeCatalogue.Empty"/> is checked in the same test because it became a singleton
    /// two assemblies share the moment the reader moved, and a broker running with a node and a
    /// terminal on one machine reaches it from both.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_thousand_draws_hold_nothing_open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parley-cat-{Guid.NewGuid():N}.json");
        var orders = AgentIdentity.Mint();

        File.WriteAllText(
            path,
            "{\"agents\":{\"" + orders + "\":{\"command\":\"claude\",\"workspace\":\"/srv/orders\"}}}");

        for (var draw = 0; draw < 1_000; draw++)
        {
            Assert.True(NodeCatalogue.TryReadFrom(path, out var catalogue, out _));
            Assert.Single(catalogue.Identities);
        }

        // A handle still open refuses this on Windows, which is the point of doing it here rather
        // than in a finally that swallows the failure.
        File.Delete(path);

        Assert.False(File.Exists(path));

        // The shared empty is the same object each time and stays empty however it is used.
        var empty = NodeCatalogue.Empty;

        Assert.Same(empty, NodeCatalogue.Empty);

        Parallel.For(0, 256, index =>
        {
            _ = index;
            _ = empty.Identities.Count;
            _ = empty.CommandFor(orders);
            _ = empty.ToString();
        });

        Assert.Empty(empty.Identities);

        // And an absent file still resolves to that same object rather than a fresh empty one,
        // which is what makes the fallback free on a path taken once per frame.
        Assert.True(NodeCatalogue.TryReadFrom(path, out var gone, out _));
        Assert.Same(empty, gone);
    }

    /// <summary>Lens 2: the line the node logs names no command and no workspace.</summary>
    /// <remarks>
    /// <para>
    /// <c>ToString</c> is documented as being for a supervisor's start-up line. Nothing shipped
    /// calls it today — checked, and recorded in the ledger as a low finding rather than fixed,
    /// because an override is reasonable to have for a debugger alone. It is probed on its
    /// documented purpose: the first caller to honour that doc writes the result to a log that
    /// outlives the process and is read by whoever can read the file, and a launch command is the
    /// one thing in this file worth not putting there. It is a full command line, and CLAUDE.md's
    /// standing constraint has credentials passed (once anything takes one) as <c>env:</c> and <c>file:</c> references
    /// precisely so they can be named in configuration without being values — a reference printed
    /// to a log is still a map to the value.
    /// </para>
    /// <para>
    /// The refusal reason is checked in the same test for the same reason: it is printed on the
    /// same path, and it is built from an exception the reader did not write.
    /// </para>
    /// </remarks>
    [Fact]
    public void What_the_node_prints_discloses_no_command()
    {
        var secret = "claude --token env:PARLEY_PROBE_TOKEN --dir /srv/private-thing";
        var path = Path.Combine(Path.GetTempPath(), $"parley-cat-{Guid.NewGuid():N}.json");

        try
        {
            // **Keyed by an identity, or this file describes a machine that can start nothing** — and a
            // start-up line reading "0 agent(s)" would satisfy every "does not disclose" assertion below
            // by having nothing to disclose.
            var orders = AgentIdentity.Mint();

            File.WriteAllText(
                path,
                "{\"agents\":{\"" + orders + "\":{\"command\":\"" + secret
                + "\",\"workspace\":\"/srv/private-thing\"}}}");

            Assert.True(NodeCatalogue.TryReadFrom(path, out var catalogue, out _));

            var line = catalogue.ToString();

            Assert.DoesNotContain("env:", line, StringComparison.Ordinal);
            Assert.DoesNotContain("PARLEY_PROBE_TOKEN", line, StringComparison.Ordinal);
            Assert.DoesNotContain("private-thing", line, StringComparison.Ordinal);
            Assert.DoesNotContain("orders", line, StringComparison.Ordinal);

            // It still says the two things it is for.
            Assert.Contains(NodeCatalogue.Machine, line, StringComparison.Ordinal);
            Assert.Contains("1 agent", line, StringComparison.Ordinal);

            // A refusal names why the file could not be read, not what was in it.
            File.WriteAllText(
                path,
                "{\"agents\":{\"orders\":{\"command\":\"" + secret + "\"");

            Assert.False(NodeCatalogue.TryReadFrom(path, out _, out var problem));
            Assert.NotNull(problem);
            Assert.DoesNotContain("PARLEY_PROBE_TOKEN", problem!, StringComparison.Ordinal);
            Assert.DoesNotContain("private-thing", problem!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Lens 3: the project files say what the move claims.</summary>
    /// <remarks>
    /// The graph walks in <c>Parley.Client.Tests</c> answer "can this reach a store". This asks the
    /// narrower question a move raises: that the reader's new home took on nothing to hold it, and
    /// that its old home is now a consumer rather than an owner. Both are one line in a file
    /// somebody edits by hand, and neither is visible in a walk that only reports its verdict.
    /// </remarks>
    [Fact]
    public void The_move_cost_one_reference_and_added_none()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        // Split on both separators rather than asking Path, which does not treat a backslash as
        // one on Linux: a csproj written on Windows would yield "..\Parley.Core\Parley.Core" there
        // and this test would pass on one platform and fail on the other. It did, on the first run.
        static string[] References(string csproj) =>
            [.. File.ReadLines(csproj)
                .Where(line => line.Contains("ProjectReference", StringComparison.Ordinal))
                .Select(line => line
                    .Split('"')
                    .FirstOrDefault(part => part.EndsWith(".csproj", StringComparison.Ordinal))
                    ?? string.Empty)
                .Select(path => path.Split('/', '\\')[^1])
                .Select(file => file.Length > 7 ? file[..^7] : string.Empty)
                .Where(name => name.Length > 0)
                .OrderBy(name => name, StringComparer.Ordinal)];

        var configuration = Path.Combine(
            root!.FullName, "src", "Parley.Configuration", "Parley.Configuration.csproj");

        // The reader's new home takes Core and nothing else. It is what makes it readable by the
        // terminal, the node and the web UI alike, and it is one edit away from not being true.
        Assert.Equal(["Parley.Core"], References(configuration));

        var headless = Path.Combine(root.FullName, "src", "Parley.Headless", "Parley.Headless.csproj");

        Assert.Contains("Parley.Configuration", References(headless));

        // The reader's file is where it says it is, and is not still where it was.
        Assert.True(File.Exists(Path.Combine(
            root.FullName, "src", "Parley.Configuration", "NodeCatalogue.cs")));
        Assert.False(File.Exists(Path.Combine(
            root.FullName, "src", "Parley.Headless", "NodeCatalogue.cs")));

        // And no file under the terminal imports the old home at all, which is the import the
        // graph walk was added to catch and the one a stale using would restore.
        var stale = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src", "Parley.Terminal"), "*.cs")
            .Where(file => File.ReadLines(file).Any(line =>
                line.StartsWith("using Parley.Headless", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(stale.Length == 0, "the terminal still imports its old home: " + string.Join(", ", stale));
    }
}
