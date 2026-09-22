using Parley.Configuration;
using Parley.Core;
using System.Reflection;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The second run of each lens over the catalogue reader's move.
/// </summary>
/// <remarks>
/// The first run asked whether the reader still answers, whether a hostile file stays inert, and
/// whether anything still names its old home. These ask the questions that one did not: what the
/// reader does while somebody is writing the file, whether an entry can be made to speak for a
/// machine it is not on, and whether the move changed the surface rather than only its address.
/// </remarks>
public sealed class ProbeTwoTests
{
    /// <summary>
    /// Lens 1: a file being rewritten under a reader is refused or read, never half-read.
    /// </summary>
    /// <remarks>
    /// This is not a hypothetical schedule. <c>Shell.Catalogue</c> reads the file on every draw
    /// and the node re-reads it every watch interval, so a person saving a catalogue in an editor
    /// is concurrent with both by construction. The reader's own remark rests on distinguishing an
    /// empty catalogue from an unreadable one; the case that would break it is a torn read that
    /// reports success with some of the entries.
    /// </remarks>
    [Fact]
    public async Task A_catalogue_being_written_is_never_half_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parley-cat-{Guid.NewGuid():N}.json");
        // Keyed by identity, because a catalogue is (018). A role-keyed fixture reads as a machine that
        // can start nothing, which would leave this probe watching a torn read of an empty answer.
        var full = Enumerable.Range(0, 5).Select(_ => AgentIdentity.Mint()).ToArray();

        string Whole() =>
            "{\"agents\":{"
            + string.Join(",", full.Select(agent =>
                $"\"{agent}\":{{\"command\":\"claude\",\"workspace\":\"/srv/{agent}\"}}"))
            + "}}";

        File.WriteAllText(path, Whole());

        var escaped = new List<string>();
        var torn = new List<string>();
        var refusals = 0;
        var reads = 0;
        var stop = new CancellationTokenSource();

        var writer = Task.Factory.StartNew(() =>
        {
            var text = Whole();

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // A save in progress, written the way an editor writes it: opened, truncated,
                    // and filled in pieces, so a reader arriving mid-way sees a prefix.
                    using var file = new FileStream(
                        path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    using var writing = new StreamWriter(file);

                    foreach (var chunk in text.Chunk(16))
                    {
                        writing.Write(chunk);
                        writing.Flush();
                    }
                }
                catch (IOException)
                {
                    // The reader holds it for a moment. Not what is under test.
                }
            }
        }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (NodeCatalogue.TryReadFrom(path, out var catalogue, out var problem))
                    {
                        Interlocked.Increment(ref reads);

                        var found = catalogue.Identities.ToArray();

                        // Success must mean the whole file. A prefix that happened to close its
                        // braces would be the defect this probe exists for.
                        if (found.Length != 0 && found.Length != full.Length)
                        {
                            lock (torn)
                            {
                                torn.Add(string.Join(",", found));
                            }
                        }

                        if (problem is not null)
                        {
                            lock (torn)
                            {
                                torn.Add("read, and still gave a reason: " + problem);
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref refusals);
                    }
                }
                catch (Exception ex)
                {
                    lock (escaped)
                    {
                        escaped.Add(ex.GetType().Name + ": " + ex.Message);
                    }

                    return;
                }
            }
        }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        await Task.WhenAll([writer, .. readers]);

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }

        Assert.True(escaped.Count == 0, "exceptions escaped the reader:\n" + string.Join("\n", escaped));
        Assert.True(torn.Count == 0, "a partial file was reported as a whole one:\n" + string.Join("\n", torn.Take(5)));

        // And the probe was not vacuous: both outcomes actually happened.
        Assert.True(reads > 0, "no read ever succeeded, so nothing was checked");
        Assert.True(refusals > 0, "no read was ever refused, so the writer never raced anybody");
    }

    /// <summary>Lens 2: an entry cannot be made to speak for a machine it is not on.</summary>
    /// <remarks>
    /// A catalogue describes one machine, and <c>Find</c> checks that rather than trusting the
    /// key. The probe writes the keys somebody would use to get around it — a role already
    /// carrying a machine, and one carrying this machine — and asks for both spellings back.
    /// </remarks>
    // **`A_catalogue_speaks_only_for_its_own_machine` stood here, and 018 removed what it probed.** The
    // catalogue was keyed by role, so a lookup had to carry a machine and refuse one that was not this
    // one — otherwise an entry for `orders` here would have answered for `orders@server-b`, and this node
    // would start a process for a repository that is not on it under a name somebody else holds. It is
    // keyed by identity now, and an identity says nothing about machines, so there is nothing here to
    // check. **The property itself is not gone and is not free**: it moved to the one place that still
    // compares a name to a machine, and `AgentFleetTests.An_agent_on_another_machine_is_never_started_from_here`
    // is where it is now held.

    [Fact]
    public void The_reader_carries_the_surface_it_had()
    {
        var type = typeof(NodeCatalogue);

        Assert.Equal("Parley.Configuration", type.Namespace);
        Assert.Equal("Parley.Configuration", type.Assembly.GetName().Name);
        Assert.True(type.IsSealed, "the reader stopped being sealed in the move");

        var surface = type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // **Written out rather than counted, which is what makes it a decision.** 018 replaced `Roles`
        // with five members, and every one of them is a choice about what a catalogue is for: it is keyed
        // by identity (`Identities`), it says nothing about what anything is called, so the names come
        // back out of the repositories (`Workspaces`, `TryResolve`, `Announceable`), and a key that is
        // not an identity is reported rather than swallowed (`Unreadable`).
        Assert.Equal(
            [
                "Announceable",
                "CommandFor",
                "DefaultFileName",
                "Empty",
                "Identities",
                "Machine",
                "PathVariable",
                "Read",
                "ReadFrom",
                "ToString",
                "TryReadFrom",
                "TryResolve",
                "Unreadable",
                "WorkspaceFor",
                "Workspaces",
            ],
            surface);

        // No constructor is public: a catalogue comes from a file or from Empty, which is what
        // keeps "listing an agent here is the consent to start it" true of a file somebody wrote.
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // And the old home does not re-export it, which would let a stale using compile.
        var headless = typeof(Parley.Headless.AgentFleet).Assembly;

        Assert.DoesNotContain(
            headless.GetTypes(),
            candidate => candidate.Name == nameof(NodeCatalogue));
    }
}
