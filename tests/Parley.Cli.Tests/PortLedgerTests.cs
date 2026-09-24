using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a port is claimed rather than probed, and that losing the record costs only a rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>The probe below is the reason this type exists.</b> Choosing "a free port" by binding one and
/// letting go is already a recorded intermittent failure in this repository's own suite — two callers
/// bind, both release, and both believe they have it. 019 makes naming a project the whole of creating
/// one, which would have put that race in front of every person who ever types <c>project add</c>.
/// </para>
/// <para>
/// So <c>Two_claims_at_once_are_never_handed_one_port</c> is written to fail against an implementation
/// that only probes, and it does: run against a version whose claim does not take the hold, twenty-four
/// concurrent claimants over sixteen rounds hand the same port out repeatedly. <b>Probe; do not
/// reason.</b>
/// </para>
/// </remarks>
public sealed class PortLedgerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-ports-" + Path.GetRandomFileName());

    public PortLedgerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every port is available, so the band's own arithmetic is what is under test.</summary>
    private static bool Anything(int port) => port >= 0;

    private static int PortOf(string endpoint) => new Uri(endpoint).Port;

    [Fact]
    public void A_first_claim_comes_from_the_bottom_of_the_band()
    {
        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var endpoint, out var problem),
            problem);

        Assert.Equal(PortLedger.BandFirst, PortOf(endpoint!));
        Assert.Equal("http://127.0.0.1:19100", endpoint);
    }

    [Fact]
    public void The_band_is_clear_of_both_operating_systems_ephemeral_ranges()
    {
        // **Not taste, and not arbitrary.** The deployment that prompted 019 serves on 51707 and 53283,
        // and both sit inside the ephemeral range — Windows' dynamic range on that machine is
        // 49152–65535, Linux's default ip_local_port_range is 32768–60999. A port there can be held
        // transiently by an outbound connection from any program on the machine, which is a logon-time
        // bind failure that happens rarely and looks like nothing.
        //
        // Asserted rather than left as a comment, because a band widened later by somebody who has not
        // read that paragraph is exactly how the flake gets manufactured.
        Assert.True(PortLedger.BandFirst > 1023, "the band reaches into the well-known range");
        Assert.True(PortLedger.BandLast < 32768, "the band reaches Linux's ephemeral floor");

        // And clear of what development tools habitually take, since a machine running Parley is
        // overwhelmingly a machine somebody develops on.
        foreach (var habitual in new[] { 3000, 5000, 5173, 8000, 8080, 9000 })
        {
            Assert.False(
                habitual >= PortLedger.BandFirst && habitual <= PortLedger.BandLast,
                $"{habitual} is inside the band");
        }
    }

    [Fact]
    public void A_second_project_is_handed_a_different_port()
    {
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var first, out _));
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("billing"), Anything, out var second, out _));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Claiming_twice_for_one_project_keeps_the_port_it_has()
    {
        // `project set` changes something else and claims again. Handing out a second port would strand
        // the first — recorded against nobody, and unavailable for ever.
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var first, out _));
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var again, out _));

        Assert.Equal(first, again);
        Assert.Single(PortLedger.Read(_root));
    }

    [Fact]
    public void A_released_port_is_handed_out_again()
    {
        // FR-014, and the reason a high-water mark was rejected: a counter cannot express a removal.
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var first, out _));
        Assert.True(PortLedger.TryRelease(_root, ProjectName.Create("orders"), out var problem), problem);

        Assert.Empty(PortLedger.Read(_root));

        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("billing"), Anything, out var second, out _));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Releasing_a_project_the_record_never_knew_is_not_a_failure()
    {
        // The record is bookkeeping. Being asked to forget something it never held is the state the
        // caller wanted, and refusing would make `project remove` fail on a machine whose record had
        // been rebuilt.
        Assert.True(PortLedger.TryRelease(_root, ProjectName.Create("orders"), out var problem), problem);
    }

    [Fact]
    public void A_port_something_else_holds_is_stepped_over()
    {
        // The bind test earns its keep here: the record has never heard of this port, and whatever the
        // band would hand out first is held by something outside the record entirely.
        //
        // **Which port that is has to be discovered rather than assumed, and this test assumed it.** It
        // bound `BandFirst` itself and claimed afterwards, which is correct only on a machine where
        // nothing already holds 19100 — and the machine most likely to run this suite is one that is
        // actually running Parley, where the first project ever named holds exactly that. It failed with
        // `SocketException` from its own setup line, before reaching a single assertion, the first time
        // the suite was run on a machine with a live 0.32.0 deployment on it.
        //
        // So: ask the ledger what it would hand out, give that port back, hold it, and ask again.
        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), out var first, out var whyNot),
            whyNot);

        var contested = PortOf(first!);

        Assert.True(PortLedger.TryRelease(_root, ProjectName.Create("orders"), out var released), released);

        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        holder.Bind(new IPEndPoint(IPAddress.Loopback, contested));
        holder.Listen();

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), out var endpoint, out var problem),
            problem);

        Assert.NotEqual(contested, PortOf(endpoint!));
    }

    [Fact]
    public void A_full_band_is_reported_and_nothing_is_created()
    {
        // Driven through the availability seam rather than by binding two hundred and fifty-six sockets,
        // which would be a test that measures the machine it runs on.
        Assert.False(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), _ => false, out var endpoint, out var problem));

        Assert.Null(endpoint);
        Assert.Contains("orders", problem, StringComparison.Ordinal);
        Assert.Contains(PortLedger.BandFirst.ToString(System.Globalization.CultureInfo.InvariantCulture), problem, StringComparison.Ordinal);

        // Nothing half-created: exhaustion is reported the same way a failed claim is.
        Assert.Empty(PortLedger.Read(_root));
    }

    // ---- reconciliation -----------------------------------------------------------------------

    [Fact]
    public void A_port_a_project_already_serves_on_is_spoken_for_even_when_the_record_forgot_it()
    {
        // **FR-013, and what makes SC-009 true.** The projects are the truth and the record is
        // bookkeeping, so a claim reads the projects before choosing — a record that has lost entries
        // costs one directory listing to repair.
        ProjectManifest.Write(
            _root,
            [new ProjectSettings(
                ProjectName.Create("orders"),
                ProjectFolder.StoreFor(_root, ProjectName.Create("orders")),
                PortLedger.Loopback(PortLedger.BandFirst),
                null)]);

        Assert.Empty(PortLedger.Read(_root));

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("billing"), Anything, out var endpoint, out var problem),
            problem);

        Assert.NotEqual(PortLedger.BandFirst, PortOf(endpoint!));
    }

    [Fact]
    public void Deleting_the_record_costs_a_rebuild_and_nothing_else()
    {
        // SC-009, driven end to end: claim, write the projects those claims describe, delete the record
        // outright, and claim again. Nothing may be handed a port a project already serves on.
        var ports = new List<int>();

        foreach (var name in new[] { "orders", "billing", "inventory" })
        {
            Assert.True(PortLedger.TryClaim(_root, ProjectName.Create(name), Anything, out var endpoint, out _));

            ports.Add(PortOf(endpoint!));
        }

        ProjectManifest.Write(
            _root,
            [.. new[] { "orders", "billing", "inventory" }.Select((name, index) =>
                new ProjectSettings(
                    ProjectName.Create(name),
                    ProjectFolder.StoreFor(_root, ProjectName.Create(name)),
                    PortLedger.Loopback(ports[index]),
                    null))]);

        File.Delete(PortLedger.PathIn(_root));

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("shipping"), Anything, out var fresh, out var problem),
            problem);

        Assert.DoesNotContain(PortOf(fresh!), ports);
    }

    [Fact]
    public void A_record_that_cannot_be_parsed_is_rebuilt_rather_than_refused()
    {
        // **The opposite of what `project.json` does, and deliberately.** That file is the only record
        // of where a broker listens and is protected accordingly; this one is derivable from the
        // projects the claim is about to read anyway. Refusing here would leave a machine unable to
        // create a project because of a file whose entire contents it can work out.
        ProjectManifest.Write(
            _root,
            [new ProjectSettings(
                ProjectName.Create("orders"),
                ProjectFolder.StoreFor(_root, ProjectName.Create("orders")),
                PortLedger.Loopback(PortLedger.BandFirst),
                null)]);

        File.WriteAllText(PortLedger.PathIn(_root), "{ \"ports\": ");

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("billing"), Anything, out var endpoint, out var problem),
            problem);

        Assert.NotEqual(PortLedger.BandFirst, PortOf(endpoint!));
    }

    [Fact]
    public void The_record_is_never_deleted_even_when_the_last_project_goes()
    {
        // Carried across from StoreLock, and load-bearing on Linux: an exclusive lock belongs to an open
        // file rather than to a name, so unlinking it on the way out would let the next two claimants
        // each create and lock a different file of one name — two holders, each sure it is the only one.
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out _, out _));
        Assert.True(PortLedger.TryRelease(_root, ProjectName.Create("orders"), out _));

        Assert.True(File.Exists(PortLedger.PathIn(_root)), "the record was deleted with the last project");
    }

    [Fact]
    public void A_band_somebody_widened_survives_the_next_claim()
    {
        // **The decision is somebody's and this code does not know why they made it.** A claim that
        // silently reverted to the built-in default would hand out a port on a range whose owner had
        // already ruled it out — and it would do so on the next ordinary `project add`, long after
        // anybody connected the two.
        File.WriteAllText(
            PortLedger.PathIn(_root),
            """{ "band": { "from": 20500, "to": 20502 }, "handed_out": {} }""");

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var endpoint, out var problem),
            problem);

        Assert.Equal(20500, PortOf(endpoint!));

        // And the widened band bounds exhaustion too, rather than the built-in one.
        Assert.False(
            PortLedger.TryClaim(_root, ProjectName.Create("billing"), _ => false, out _, out var full));

        Assert.Contains("20500", full, StringComparison.Ordinal);
        Assert.Contains("20502", full, StringComparison.Ordinal);
    }

    [Fact]
    public void A_band_that_makes_no_sense_is_the_default_rather_than_a_refusal()
    {
        // A band nobody set is not a decision to preserve, and neither is one that cannot be read. The
        // alternative is a machine unable to create a project because of a hand edit in a file whose
        // whole purpose is bookkeeping.
        File.WriteAllText(
            PortLedger.PathIn(_root),
            """{ "band": { "from": 900, "to": 12 }, "handed_out": {} }""");

        Assert.True(
            PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out var endpoint, out var problem),
            problem);

        Assert.Equal(PortLedger.BandFirst, PortOf(endpoint!));
    }

    [Fact]
    public void The_record_uses_the_member_names_the_contract_gives()
    {
        // Somebody reads this file, and `contracts/` is what they read it against. A key spelled
        // differently in the code is a contract nobody can check against the thing it describes.
        Assert.True(PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out _, out _));

        var written = File.ReadAllText(PortLedger.PathIn(_root));

        Assert.Contains("\"handed_out\"", written, StringComparison.Ordinal);
        Assert.Contains("\"orders\"", written, StringComparison.Ordinal);
    }

    // ---- the race -------------------------------------------------------------------------------

    [Fact]
    public void Two_claims_at_once_are_never_handed_one_port()
    {
        // **SC-008, and the whole reason this type is not a free-port helper.** Written to fail against
        // an implementation that only probes: without the hold, every claimant reads the same record,
        // sees the same port free, binds it, releases it and records it.
        //
        // Threads rather than the thread pool, for the reason ManifestCurrencyTests records: a claimant
        // that must run is a thread, not a work item, and a pool saturated by the rest of the suite
        // would let this pass by never starting. Released together on a barrier, so the
        // read-choose-write windows genuinely overlap.
        const int claimants = 24;

        var barrier = new Barrier(claimants);
        var handed = new ConcurrentBag<(string Project, int Port)>();
        var refused = new ConcurrentBag<string>();

        var threads = Enumerable.Range(0, claimants).Select(index =>
        {
            var thread = new Thread(() =>
            {
                barrier.SignalAndWait();

                // Caught, because an unhandled exception on a background thread does not fail this test
                // -- it hangs it, and a stall says nothing about what went wrong.
                try
                {
                    if (PortLedger.TryClaim(
                            _root, ProjectName.Create($"p{index}"), Anything, out var endpoint, out var problem))
                    {
                        handed.Add(($"p{index}", PortOf(endpoint!)));
                    }
                    else
                    {
                        refused.Add(problem ?? "declined");
                    }
                }
                catch (Exception failure)
                {
                    refused.Add($"{failure.GetType().Name}: {failure.Message}");
                }
            })
            { IsBackground = true };

            thread.Start();

            return thread;
        }).ToArray();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Contention is ordinary here rather than fatal -- the hold is transient and the claim retries --
        // so a refusal inside the budget would be a defect rather than a tolerated outcome.
        Assert.Empty(refused);

        Assert.Equal(claimants, handed.Count);

        // The claim itself: no two projects hold one port.
        var duplicates = handed
            .GroupBy(entry => entry.Port)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} went to {string.Join(", ", group.Select(e => e.Project).Order(StringComparer.Ordinal))}")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"{duplicates.Count} port(s) were handed out twice: {string.Join("; ", duplicates)}");

        // And the record agrees with what the callers were told, rather than merely being internally
        // consistent: a last writer that overwrote the others would leave every caller holding a port
        // nothing recorded.
        var recorded = PortLedger.Read(_root);

        Assert.Equal(claimants, recorded.Count);

        foreach (var (project, port) in handed)
        {
            Assert.True(
                recorded.TryGetValue(project, out var written) && written == port,
                $"'{project}' was told {port} and the record says {(recorded.TryGetValue(project, out var w) ? w.ToString(System.Globalization.CultureInfo.InvariantCulture) : "nothing")}");
        }
    }

    [Fact]
    public void A_claim_waits_for_a_hold_rather_than_failing_on_contention()
    {
        // The one place this differs from StoreLock, stated as a test. Two brokers on one store is a
        // defect and is refused outright; two people running `project add` at the same moment is a
        // Tuesday, so the claim retries under a budget.
        using var held = new FileStream(
            PortLedger.PathIn(_root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var claimed = false;
        string? problem = null;

        var claimant = new Thread(() =>
            claimed = PortLedger.TryClaim(_root, ProjectName.Create("orders"), Anything, out _, out problem))
        { IsBackground = true };

        claimant.Start();

        // Long enough that a claim which failed immediately would have finished, short enough to stay
        // well inside the budget.
        Assert.False(claimant.Join(TimeSpan.FromMilliseconds(400)), "the claim gave up rather than waiting");

        held.Dispose();

        Assert.True(claimant.Join(TimeSpan.FromSeconds(15)), "the claim never completed after the hold went");
        Assert.True(claimed, problem);
    }
}
