using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That two projects asking for one checkout at the same moment produce one process.
/// </summary>
/// <remarks>
/// <para>
/// <b>One checkout, one process.</b> The resident table is one per process, keyed by identity, so
/// that a checkout answering in two projects is started once however many projects ask. The hazard on the
/// other side is two nodes launching one working directory.
/// </para>
/// <para>
/// <b>Each project hosts its own node loop, and they share that table.</b> So the two calls below are not
/// contrived — they are what two projects asking at the same instant looks like from inside one broker
/// process.
/// </para>
/// <para>
/// The catalogued command appends a line to a file, because what is being counted is **how many processes
/// were launched**, and a second one that the table no longer holds is invisible from the table. That was
/// the worse half of the defect this found: the loser of the race was overwritten in the map, so
/// <c>StopEveryoneAsync</c> would never stop it.
/// </para>
/// </remarks>
public sealed class OneCheckoutOneProcessTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-once-" + Path.GetRandomFileName());

    public OneCheckoutOneProcessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Evidence => Path.Combine(_root, "launched.txt");

    /// <summary>
    /// A command that leaves one line behind per launch and then genuinely stays alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Staying alive is what makes this measure the right thing.</b> A resident that has already exited
    /// makes the second caller's restart correct rather than a race — that is the rule
    /// <c>A_turn_for_a_resident_whose_process_has_died_is_cleared_and_named</c> holds — so a fixture whose
    /// process does not outlive the second call cannot measure the race at all.
    /// </para>
    /// <para>
    /// <see cref="StandingAgent"/> carries why the command is not the obvious one — and this probe is the
    /// reason that file exists. It believed it was launching something that lived thirty seconds and was
    /// launching something that exited in twenty milliseconds, so under load its second caller kept
    /// finding a dead resident and correctly starting a replacement, which this counted as the defect it
    /// was hunting.
    /// </para>
    /// </remarks>
    private string Marking(int attempt) => StandingAgent.Recording(Evidence, attempt);

    [Fact]
    public async Task Two_projects_asking_at_the_same_moment_start_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");

        _ = Directory.CreateDirectory(workspace);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        var reports = new List<string>();

        // The repository declares the name each project calls it, which is how a name resolves to an
        // identity. Two projects, two names, one checkout.
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } },
                "parley-beta":  { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "beta" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            ct);

        // **Repeated, because one attempt at a race proves nothing.** The window between the check and the
        // record is short, so a single pass can miss it and report a guarantee that is not there. This
        // repository's practice for a suspected race is many attempts under load — 006's refill probe drove
        // four thousand draws before it reported anything.
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // Rewritten per attempt so each launch stamps which attempt it belongs to.
            await File.WriteAllTextAsync(
                cataloguePath,
                $$"""
                {
                  "agents": {
                    "{{identity}}": {
                      "command": {{System.Text.Json.JsonSerializer.Serialize(Marking(attempt))}},
                      "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                    }
                  }
                }
                """,
                ct);

            reports.Add(await OnceAsync(NodeCatalogue.ReadFrom(cataloguePath), ct));
        }

        // Guarded against passing by vacuity: a run where nothing ever started measured nothing, and
        // would satisfy "never started twice" perfectly.
        var started = reports.Sum(Starts);

        Assert.True(started > 0, "nothing was launched, so this probe measured nothing");

        // Which attempt announced two launches, and what it said — theorising about a race is how it stays
        // unfixed, so the report itself travels with the failure.
        var twice = reports
            .Select((report, attempt) => (attempt, report, starts: Starts(report)))
            .Where(each => each.starts > 1)
            .Select(each => $"attempt {each.attempt} announced {each.starts} launches: {Flatten(each.report)}")
            .ToArray();

        Assert.True(twice.Length == 0, string.Join(" | ", twice));
        Assert.Equal(Attempts, started);
    }

    /// <summary>Counts the launches a run of the command announced.</summary>
    /// <remarks>
    /// Matched against the sentence <c>StartAsync</c> writes when it has actually launched something, rather
    /// than against the word "started", which also appears in the sentence naming agents somebody else's node
    /// is already running.
    /// </remarks>
    private static int Starts(string report) =>
        report.Split('\n').Count(line => line.Contains(": started (", StringComparison.Ordinal));

    /// <summary>Puts a multi-line report on one line, so a failure message stays one line.</summary>
    private static string Flatten(string report) =>
        string.Join(
            " / ",
            report.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    private const int Attempts = 10;

    /// <summary>
    /// Runs one pair of simultaneous starts and hands back what the command itself reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The report is the measurement, and counting launched processes is not.</b> A second process is
    /// started for two different reasons — the race this exists to catch, and the wholly correct restart of
    /// a resident that has died, which <c>A_turn_for_a_resident_whose_process_has_died_is_cleared_and_named</c>
    /// requires. **Nothing outside the command can tell those apart after the fact**, because the loser is
    /// gone from the table either way.
    /// </para>
    /// <para>
    /// <c>StartAsync</c> announces the two cases differently — a fresh <c>started</c> against a
    /// <c>restarted … the previous process had gone</c> — so two *fresh* launches in one attempt is the
    /// guard having admitted both callers, and that is unambiguous. An earlier version of this probe
    /// asserted on lines appended by the launched processes, which cannot make that distinction: it passed
    /// alone and failed in the full suite, where load widens the window in which the first process dies.
    /// </para>
    /// <para>
    /// <b>The distinction had to be built before it could be asserted</b>, and doing so found the leak
    /// beside it: the replacement path overwrote the dead resident instead of disposing it, dropping a
    /// Process handle and two output pumps every time an agent died.
    /// </para>
    /// </remarks>
    private static async Task<string> OnceAsync(NodeCatalogue catalogue, CancellationToken ct)
    {
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        // Both callers write into one writer, so it has to be safe for both of them.
        var said = new StringWriter();
        var report = TextWriter.Synchronized(said);

        // Both node loops reach the start for the same identity at once.
        await Task.WhenAll(
            Task.Run(
                async () => await NodeCommand.StartAsync(
                    new Wire.StartAgent { Agent = "orders@" + NodeCatalogue.Machine, Because = "alpha asked" },
                    catalogue, "alpha", residents, report, ct),
                ct),
            Task.Run(
                async () => await NodeCommand.StartAsync(
                    new Wire.StartAgent { Agent = "billing@" + NodeCatalogue.Machine, Because = "beta asked" },
                    catalogue, "beta", residents, report, ct),
                ct));

        // One entry, whatever happened to the processes.
        var resident = Assert.Single(residents).Value;

        await resident.DisposeAsync();

        return said.ToString();
    }
}
