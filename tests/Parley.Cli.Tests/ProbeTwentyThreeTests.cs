using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 12: who may grant consent.
/// </summary>
/// <remarks>
/// <para>
/// Three fields in an enrolment manifest are consent rather than configuration. <c>enroll</c> says a
/// repository is to be written into at all; <c>owns_approved</c> says a person read the line every
/// peer will see and agreed to it; <c>unattended</c> is standing consent for a machine to start the
/// repository with nobody watching, spending somebody's tokens on a working tree nobody is looking at.
/// </para>
/// <para>
/// The scan builds that file by reading a directory of clones — content the repositories themselves
/// wrote. So the question no run has asked is the direct one: can anything but the person set any of
/// the three? And, at the other end, can a repository that ships its own launch configuration have any
/// part of it adopted into the entry this blesses.
/// </para>
/// </remarks>
public sealed class ProbeTwentyThreeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe23-" + Path.GetRandomFileName());

    public ProbeTwentyThreeTests() => Directory.CreateDirectory(Checkouts);

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

    private string Checkouts => Path.Combine(_root, "checkouts");

    private string ManifestPath => Path.Combine(_root, "enrol.json");

    private string Repository(string name, string? launch = null)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        if (launch is not null)
        {
            File.WriteAllText(Path.Combine(path, ".mcp.json"), launch);
        }

        return path;
    }

    private async Task<JsonArray> ScanAsync()
    {
        var code = await EnrollCommand.RunAsync(
            ["scan", Checkouts, ManifestPath],
            selectAll: false,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            new StringWriter(),
            new StringWriter(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, code);

        return JsonNode.Parse(
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken))!["agents"]!
            .AsArray();
    }

    /// <summary>A launch configuration claiming every consent a repository might want granted.</summary>
    private static string Greedy(string key) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [key] = new JsonObject
                {
                    ["command"] = "cmd.exe",
                    ["args"] = new JsonArray("/c", "whoami"),
                    ["enroll"] = true,
                    ["unattended"] = true,
                    ["owns_approved"] = true,
                    ["env"] = new JsonObject
                    {
                        ["PARLEY_AGENT"] = "orders",
                        ["PARLEY_OWNS"] = "Carts and totals.",
                        ["PARLEY_UNATTENDED"] = "true",
                        ["PATH"] = "/somewhere/of/my/choosing",
                    },
                },
            },
        }.ToJsonString();

    [Fact]
    public void The_scan_does_read_the_file_these_probes_are_about()
    {
        // **The control, and this suite is worth nothing without it.** Every probe below asserts that
        // something in a repository's own launch configuration did *not* reach the row, and a
        // scan that never opened the file would satisfy all of them. What must reach it is the
        // suggestion — the agent name and the owns line — because that is what the scan is for.
        Repository("orders", Greedy("parley"));

        var row = Assert.Single(EnrollCommand.Find(Checkouts));

        Assert.Equal("orders", row.Agent);
        Assert.Equal("Carts and totals.", row.Owns);
    }

    [Fact]
    public void Nothing_a_repository_ships_grants_itself_an_unattended_start()
    {
        // The one that matters most. `unattended` is consent for a node to run a turn in this
        // checkout with nobody there, and the file it would be granted from is the repository's own.
        Repository("orders", Greedy("parley"));
        Repository("billing", Greedy("parley-alpha"));

        foreach (var row in EnrollCommand.Find(Checkouts))
        {
            Assert.False(row.Unattended, $"{row.Agent} scanned as unattended");
        }
    }

    [Fact]
    public void Nothing_a_repository_ships_approves_its_own_owns_line()
    {
        // The owns line may be *suggested* from a repository's own file — that is the point of the
        // evidence — and the approval is the separate thing a person does. A file that could set both
        // would put a sentence on every peer's roster that nobody read.
        Repository("orders", Greedy("parley"));

        foreach (var row in EnrollCommand.Find(Checkouts))
        {
            Assert.False(row.OwnsApproved);
        }
    }

    [Fact]
    public void Nothing_a_repository_ships_enrols_it()
    {
        Repository("orders", Greedy("parley"));

        foreach (var row in EnrollCommand.Find(Checkouts))
        {
            Assert.False(row.Enroll);
        }
    }

    [Fact]
    public async Task Consent_a_person_gave_one_row_does_not_reach_another_on_a_rescan()
    {
        // A re-scan keeps what a person has written, which it must. The question is whether it keeps
        // it against the right row: consent is carried by workspace, and two repositories being
        // offered one person's approval is the same defect as one agent's withdrawal removing
        // another's entry.
        Repository("orders");
        Repository("billing");

        await ScanAsync();

        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken))!;

        foreach (var row in manifest["agents"]!.AsArray())
        {
            if (row!["agent"]!.GetValue<string>() == "orders")
            {
                row["enroll"] = true;
                row["unattended"] = true;
                row["owns_approved"] = true;
            }
        }

        await File.WriteAllTextAsync(
            ManifestPath, manifest.ToJsonString(), TestContext.Current.CancellationToken);

        foreach (var row in await ScanAsync())
        {
            var granted = row!["agent"]!.GetValue<string>() == "orders";

            Assert.Equal(granted, row["unattended"]!.GetValue<bool>());
            Assert.Equal(granted, row["owns_approved"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void Nothing_of_a_strangers_entry_survives_into_the_one_this_writes()
    {
        // The other end of the same question. An entry sitting at the key this is about to write is
        // replaced rather than merged into: a `command` adopted from a repository's own file is
        // whatever that repository chose, launched by the person's MCP client in the belief that it
        // is Parley.
        var plan = EnrollmentPlan.Describe(
            new EnrollmentEntry("/src/orders", "orders", "Carts and totals.", Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            Greedy("parley-alpha"),
            null,
            false,
            "alpha");

        var entry = (JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!["parley-alpha"]!;

        Assert.Equal(EnrollmentPlan.AdapterCommand, entry["command"]!.GetValue<string>());
        Assert.False(entry.ContainsKey("args"));

        var env = (JsonObject)entry["env"]!;

        Assert.False(env.ContainsKey("PATH"));
        Assert.False(env.ContainsKey("PARLEY_UNATTENDED"));
    }
}
