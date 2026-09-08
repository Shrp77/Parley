using Parley.Configuration;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That moving the catalogue reader changed what it does in no way at all.
/// </summary>
/// <remarks>
/// <para>
/// Written for the review gate of the move rather than for the reader, which has its own tests
/// in <c>Parley.Headless.Tests</c> and keeps them. These ask the three questions the move itself
/// raises: does it still answer identically, can a file reached through its new home repaint a
/// surface, and does anything still look for it where it used to be.
/// </para>
/// <para>
/// <b>The first draft of all three failed, and all three were the probe's fault.</b> It asserted
/// a missing file is a refusal — the reader's own remark says the opposite and gives the reason;
/// it wrote a raw escape byte into a JSON string, which is invalid JSON, so it tested the parser
/// rather than the reader; and it matched its own assertion text when it walked the tree. Recorded
/// because a probe that fails for the wrong reason proves as little as one that passes vacuously.
/// </para>
/// </remarks>
public sealed class ProbeTests
{
    /// <summary>Lens 1: the reader behaves identically after the move.</summary>
    [Fact]
    public void The_moved_reader_answers_as_it_did()
    {
        var problems = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"parley-cat-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "agents": {
                    "orders":    { "command": "claude", "workspace": "C:/repo/orders" },
                    "billing":   { "command": "claude", "workspace": "C:/repo/billing" },
                    "Not-A-Name": { "command": "claude", "workspace": "C:/repo/x" }
                  }
                }
                """);

            if (!NodeCatalogue.TryReadFrom(path, out var catalogue, out var why))
            {
                problems.Add($"a well-formed catalogue was refused: {why}");
            }
            else
            {
                if (!catalogue.Roles.Contains("orders") || !catalogue.Roles.Contains("billing"))
                {
                    problems.Add($"roles lost: {string.Join(",", catalogue.Roles)}");
                }

                // A role that is not a well-formed agent name still gets a row -- the reader's own
                // remark says dropping it would hide a hand-edited mistake.
                if (!catalogue.Roles.Contains("Not-A-Name"))
                {
                    problems.Add("a malformed role was dropped rather than kept");
                }
            }

            // Empty is still empty, and still safe to ask anything of.
            if (NodeCatalogue.Empty.Roles.Count != 0)
            {
                problems.Add("Empty is not empty");
            }

            // An absent file is readable and empty, which is the contract TryReadFrom documents:
            // deleting a catalogue is a deliberate act, and only a half-written one is a refusal.
            if (!NodeCatalogue.TryReadFrom(path + ".missing", out var absent, out var missing))
            {
                problems.Add("a missing catalogue was refused rather than read as empty");
            }
            else if (absent.Roles.Count != 0 || missing is not null)
            {
                problems.Add("a missing catalogue was read as something other than empty");
            }

            // A half-written one is the refusal, and it says why.
            File.WriteAllText(path, "{ \"agents\": { \"orders\": ");

            if (NodeCatalogue.TryReadFrom(path, out _, out var torn))
            {
                problems.Add("a truncated catalogue was accepted");
            }
            else if (string.IsNullOrWhiteSpace(torn))
            {
                problems.Add("a truncated catalogue was refused without saying why");
            }

            // And a blank path is still made the caller's problem rather than resolved.
            try
            {
                _ = NodeCatalogue.TryReadFrom("  ", out _, out _);

                problems.Add("a blank path was resolved to something");
            }
            catch (ArgumentException)
            {
                // As documented.
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Lens 2: a hostile catalogue cannot repaint anything through the moved reader.</summary>
    [Fact]
    public void A_hostile_catalogue_stays_inert()
    {
        var escape = (char)27;
        var path = Path.Combine(Path.GetTempPath(), $"parley-cat-{Guid.NewGuid():N}.json");

        try
        {
            // The escape is written as \u001b rather than as a raw byte, which is the case that
            // matters: a raw control character makes the file invalid JSON and the reader never
            // gets to it. This file parses, and carries the escape in the value.
            File.WriteAllText(
                path,
                "{\"agents\":{\"orders\":{\"command\":\"claude\\u001b[2J\","
                + "\"workspace\":\"C:/repo\\u001b[31m\"}}}");

            Assert.True(NodeCatalogue.TryReadFrom(path, out var catalogue, out _));

            // The reader does not neutralise -- the surfaces do, as they draw. What matters is
            // that it does not *act* on it either: no execution, no interpretation, just text.
            var command = catalogue.CommandFor(Parley.Core.AgentName.Create("orders@" + NodeCatalogue.Machine));

            Assert.NotNull(command);
            Assert.Contains(escape, command!);

            // And the surface that draws it makes it inert, which is where that belongs.
            Assert.DoesNotContain(escape, Parley.Top.Safe.Text(command!));
            Assert.DoesNotContain(escape, Parley.Top.Safe.Text(catalogue.WorkspaceFor(
                Parley.Core.AgentName.Create("orders@" + NodeCatalogue.Machine))!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Lens 3: nothing still names the reader's old home.</summary>
    [Fact]
    public void No_source_still_looks_for_the_reader_in_headless()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        // Assembled rather than written, so this file does not match itself -- the first draft did,
        // and reported the probe as the finding.
        var old = string.Join(".", "Parley", "Headless", "NodeCatalogue");
        var mine = Path.GetFileName(GetSourcePath());
        var stale = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root!.FullName, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(file), mine, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(old, StringComparison.Ordinal))
            {
                stale.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            stale.Count == 0,
            "these still name the reader's old home: " + string.Join(", ", stale));
    }

    private static string GetSourcePath(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
