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
/// <b>018's headline claim, and its own quickstart calls it one of the two worth doubting.</b> A resident
/// table used to belong to a node; 018 made it one per process and keyed it by identity, so that a checkout
/// answering in two projects is started once however many projects ask. The hazard on the other side is the
/// one 009's FR-013 partitioned the catalogues to avoid: two nodes launching one working directory.
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
    /// A command that leaves one line behind per launch and then stays alive.
    /// </summary>
    /// <remarks>
    /// <b>Staying alive is what makes this measure the right thing.</b> The first version exited at once,
    /// so the second caller found a resident that was no longer running and started a replacement — which
    /// is correct (<c>A_turn_for_a_resident_whose_process_has_died_is_cleared_and_named</c> holds the same
    /// rule) and is not the race. It counted three legitimate restarts as three defects, under load, in the
    /// full suite only. A live process is what the second caller is supposed to find.
    /// </remarks>
    private string Marking(int attempt) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c echo {attempt} >> \"{Evidence}\" & ping -n 30 127.0.0.1"
            : $"sh -c \"echo {attempt} >> '{Evidence}'; sleep 30\"";

    [Fact]
    public async Task Two_projects_asking_at_the_same_moment_start_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");

        _ = Directory.CreateDirectory(workspace);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);

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

            await OnceAsync(NodeCatalogue.ReadFrom(cataloguePath), ct);
        }

        var launches = File.Exists(Evidence)
            ? (await File.ReadAllLinesAsync(Evidence, ct)).Count(l => l.Trim().Length > 0)
            : 0;

        // Guarded against passing by vacuity: if nothing launched at all, one-per-attempt is not what happened.
        Assert.True(launches > 0, "nothing was launched, so this test measured nothing");

        // Which attempt started twice, if any — theorising about a race is how it stays unfixed.
        var perAttempt = (await File.ReadAllLinesAsync(Evidence, ct))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .GroupBy(l => l)
            .Where(g => g.Count() > 1)
            .Select(g => $"attempt {g.Key} started {g.Count()} times")
            .ToArray();

        Assert.True(perAttempt.Length == 0, string.Join("; ", perAttempt));
        Assert.Equal(Attempts, launches);
    }

    private const int Attempts = 10;

    private async Task OnceAsync(NodeCatalogue catalogue, CancellationToken ct)
    {
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        // Both node loops reach the start for the same identity at once.
        await Task.WhenAll(
            Task.Run(
                async () => await NodeCommand.StartAsync(
                    new Wire.StartAgent { Agent = "orders@" + NodeCatalogue.Machine, Because = "alpha asked" },
                    catalogue, "alpha", residents, TextWriter.Null, ct),
                ct),
            Task.Run(
                async () => await NodeCommand.StartAsync(
                    new Wire.StartAgent { Agent = "billing@" + NodeCatalogue.Machine, Because = "beta asked" },
                    catalogue, "beta", residents, TextWriter.Null, ct),
                ct));

        // One entry, whatever happened to the processes.
        var resident = Assert.Single(residents).Value;

        // **And it is still running.** A resident that had already exited would make the second caller's
        // restart correct rather than a race — that is the rule
        // `A_turn_for_a_resident_whose_process_has_died_is_cleared_and_named` holds — and the count below
        // could not tell the two apart. An earlier version of this probe launched something that exited at
        // once and reported three legitimate restarts as three defects, under load, in the full suite only.
        Assert.True(resident.IsAlive, "the launched process died before the second caller looked");

        // Let whatever launched write its line before the next attempt.
        await Task.Delay(250, ct);

        await resident.DisposeAsync();
    }
}
