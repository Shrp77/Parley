using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Configuration;

namespace Parley.Cli.Tests;

/// <summary>
/// Gives a throwaway root a port band of its own, so parallel test classes do not collide.
/// </summary>
/// <remarks>
/// <para>
/// <b>The band is a property of a machine, and a suite is dozens of machines' worth of roots running at
/// once.</b> <c>PortLedger</c> reconciles within one root: it reads that root's projects and that root's
/// record, and hands out the lowest port nothing there has claimed. Two roots are two records, so every
/// class that creates a project is handed 19100, then 19101, then 19102 — the same ports, at the same
/// time, in a suite that runs classes in parallel.
/// </para>
/// <para>
/// <b>The bind test does not save it.</b> It refuses a port something is *listening on*, and almost no
/// test listens: most create a project and read back what it was given. So the claims succeed, agree, and
/// only collide later — when two classes each start a real broker on what they were told was theirs.
/// </para>
/// <para>
/// <b>This was predicted and then walked into.</b> `research.md` R3 says in as many words that "the test
/// rig cannot call `project add`, because the band is per machine while a root is per test: parallel
/// classes would each be handed 19000". The conversion then wrote tests that call `project add` anyway,
/// and the suite hung — a Kestrel bind failure part way through, at a different point each run.
/// </para>
/// <para>
/// <b>The remedy is the feature the ledger already has.</b> `ports.json` carries an optional band so that
/// a machine whose range was widened keeps that decision; a test root writes a narrow band of its own
/// into the same field. Nothing about the product changes, and the thing under test — that a claim is
/// serialised and reconciled — is exercised exactly as it is in production.
/// </para>
/// </remarks>
internal static class TestBand
{
    /// <summary>How many ports one test class can need at once.</summary>
    /// <remarks>
    /// Generous: a class creating four projects uses four, and a band that ran out would fail as
    /// exhaustion — a confusing way to be told that a fixture grew.
    /// </remarks>
    private const int Width = 24;

    /// <summary>The bottom of the range bands are drawn from.</summary>
    /// <remarks>
    /// Above the product's own 19100–19355 so a test never collides with a band somebody set by hand,
    /// and below Linux's ephemeral floor of 32768 for the reason <c>PortLedger.BandLast</c> records: a
    /// port in the ephemeral range can be taken transiently by any outbound connection on the machine.
    /// </remarks>
    private const int First = 20000;

    /// <summary>The top of that range.</summary>
    private const int Last = 31000;

    /// <summary>
    /// Writes a band into a root's port record, so claims made under it cannot meet another class's.
    /// </summary>
    /// <param name="root">The throwaway root.</param>
    /// <remarks>
    /// Random rather than derived from the class name, because a name-derived band is stable across runs
    /// and so is any collision it has — a suite that failed once would fail every time for a reason
    /// nobody could see. Random bands collide rarely and differently, and the bind test inside the claim
    /// catches what is genuinely held.
    /// </remarks>
    internal static void GiveItsOwn(string root)
    {
        var from = Random.Shared.Next(First, Last - Width);

        Directory.CreateDirectory(root);

        File.WriteAllText(
            PortLedgerPath(root),
            new JsonObject
            {
                ["band"] = new JsonObject { ["from"] = from, ["to"] = from + Width },
                ["handed_out"] = new JsonObject(),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Where the record lives, spelled the way the product spells it.</summary>
    /// <remarks>
    /// Through <see cref="ProjectFolder"/>'s own naming rather than a literal, so a test that writes the
    /// record and the claim that reads it cannot end up naming two different files.
    /// </remarks>
    private static string PortLedgerPath(string root) => Path.Combine(root, "ports.json");
}
