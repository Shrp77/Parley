using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley enroll &lt;directory&gt;</c>: one verb, no file for anybody to author (FR-040).
/// </summary>
/// <remarks>
/// <para>
/// The three-step form named its first step <c>scan</c>, which was honest while that step only
/// looked. It wrote a manifest, a second step planned a change to it, and a third applied it —
/// and the manifest was a round trip through the disk between two halves of one operation, since
/// scanning built the rows in memory and applying read them straight back.
/// </para>
/// <para>
/// <b>What is checked here is that the file is gone, not merely unused.</b> A collapse that went
/// on writing a manifest somewhere would pass every test about the repositories it configured, so
/// each case below asserts the directory afterwards holds nothing but the checkouts.
/// </para>
/// </remarks>
public sealed class OneVerbEnrolmentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-oneverb-" + Path.GetRandomFileName());

    public OneVerbEnrolmentTests()
    {
        Directory.CreateDirectory(_root);
        Repository("Acme.Orders");
        Repository("Acme.Billing");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Checkouts => Path.Combine(_root, "co");

    private void Repository(string name) =>
        Directory.CreateDirectory(Path.Combine(Checkouts, name, ".git"));

    private static string LaunchFile(string repository) => Path.Combine(repository, ".mcp.json");

    private async Task<(int Code, string Out, string Error)> RunAsync(
        bool? selectAll, bool describe, params string[] arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.RunAsync(
            arguments,
            selectAll,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            output,
            error,
            TestContext.Current.CancellationToken,
            describe: describe);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task One_verb_configures_the_repositories_it_is_pointed_at()
    {
        var (code, said, complained) = await RunAsync(selectAll: true, describe: false, Checkouts);

        Assert.True(code == 0, $"exit {code}. stderr:\n{complained}\nstdout:\n{said}");

        // Both repositories configured, from one command naming one directory.
        Assert.True(File.Exists(LaunchFile(Path.Combine(Checkouts, "Acme.Orders"))));
        Assert.True(File.Exists(LaunchFile(Path.Combine(Checkouts, "Acme.Billing"))));
    }

    [Fact]
    public async Task Every_repository_enrolled_is_catalogued_so_a_peer_can_start_it()
    {
        // **FR-043, and it was dead code.** `EnrollmentPlan` gates the catalogue write on
        // `EnrollmentEntry.Unattended`, which was carried from a worksheet row somebody ticked —
        // and 019 deleted the worksheet. Nothing on this path set it afterwards and nothing on the
        // terminal's path set it either, so every repository enrolled from a folder got a launch
        // configuration and no catalogue line.
        //
        // What that produces is the state `Onboarding`'s own summary warns against: enrolled,
        // addressable and unstartable — an agent that cannot be started cannot answer. And the
        // screen asking for consent said, in as many words, that each repository was being
        // catalogued so a peer's request could start it with nobody watching. **The door described
        // the reversal and the code never got it**, which is worse than the feature being absent:
        // the operator was told it had happened.
        var (code, said, complained) = await RunAsync(selectAll: true, describe: false, Checkouts);

        Assert.True(code == 0, $"exit {code}. stderr:\n{complained}\nstdout:\n{said}");

        var catalogue = Path.Combine(_root, "node.json");

        Assert.True(
            File.Exists(catalogue),
            "nothing was catalogued, so no peer's request can start any of these repositories");

        // Both of them, by workspace. Asserted on the file rather than on what the command printed,
        // because what the command printed is exactly what was true while this was broken.
        var written = await File.ReadAllTextAsync(catalogue, TestContext.Current.CancellationToken);

        Assert.Contains("Acme.Orders", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Acme.Billing", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_writes_a_manifest_for_a_person_to_author()
    {
        _ = await RunAsync(selectAll: true, describe: false, Checkouts);

        // **The point of FR-040, asserted as an absence.** A collapse that kept writing the file
        // somewhere would satisfy every other test here. `.mcp.json` inside each checkout is the
        // launch configuration and is supposed to be there; what must not exist is a worksheet.
        var authored = Directory
            .EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ".mcp.json", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), "node.json", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            authored.Length == 0,
            $"a file was written for somebody to fill in: {string.Join(", ", authored)}");
    }

    [Fact]
    public async Task Describing_changes_nothing_at_all()
    {
        var (code, said, complained) = await RunAsync(selectAll: true, describe: true, Checkouts);

        Assert.True(code == 0, $"exit {code}. stderr:\n{complained}\nstdout:\n{said}");

        // FR-042, and the property `plan` had: not "writes less", but writes nothing. Asserted over
        // the whole tree rather than over the two files this happens to touch, because a describe
        // that left a catalogue behind would be a describe that configured the machine.
        Assert.False(File.Exists(LaunchFile(Path.Combine(Checkouts, "Acme.Orders"))));
        Assert.False(File.Exists(LaunchFile(Path.Combine(Checkouts, "Acme.Billing"))));
        Assert.False(File.Exists(Path.Combine(_root, "node.json")));
    }

    [Fact]
    public async Task Choosing_none_configures_nothing_and_says_so()
    {
        var (code, said, complained) = await RunAsync(selectAll: false, describe: false, Checkouts);

        // Refused rather than reported as a success that did nothing: somebody who ran this meant
        // to enrol something, and an exit 0 with no output would read as "already done".
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(LaunchFile(Path.Combine(Checkouts, "Acme.Orders"))));
        Assert.Contains("Nothing was configured", complained + said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_that_is_not_one_is_refused_by_name()
    {
        var missing = Path.Combine(_root, "nowhere");

        var (code, _, complained) = await RunAsync(selectAll: true, describe: false, missing);

        Assert.NotEqual(0, code);
        Assert.Contains(missing, complained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_holding_no_repositories_is_refused_by_name()
    {
        var empty = Path.Combine(_root, "empty");

        Directory.CreateDirectory(empty);

        var (code, _, complained) = await RunAsync(selectAll: true, describe: false, empty);

        Assert.NotEqual(0, code);
        Assert.Contains(empty, complained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_repository_already_says_it_owns_is_carried_forward()
    {
        // FR-048: the removed manifest was the one place a person could type this, and taking it
        // away must not lose the lines that exist. They never came from the file — `Discover` reads
        // them out of the repository's own launch configuration, which is where they were written.
        var orders = Path.Combine(Checkouts, "Acme.Orders");

        var existing = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["parley"] = new JsonObject
                {
                    ["env"] = new JsonObject
                    {
                        ["PARLEY_AGENT"] = "orders",
                        ["PARLEY_OWNS"] = "Carts, totals and the checkout API.",
                    },
                },
            },
        };

        await File.WriteAllTextAsync(
            LaunchFile(orders), existing.ToJsonString(), TestContext.Current.CancellationToken);

        // Asserted on the file rather than on the report. A first version of this read stdout, and
        // failed — not because the line was lost but because describing does not echo it, which is
        // a test asserting the wrong surface. What FR-048 promises is that the line survives, and
        // the file is where it either survives or does not.
        var (code, said, complained) = await RunAsync(selectAll: true, describe: false, Checkouts);

        Assert.True(code == 0, $"exit {code}. stderr:\n{complained}\nstdout:\n{said}");

        var after = await File.ReadAllTextAsync(LaunchFile(orders), TestContext.Current.CancellationToken);

        Assert.Contains("Carts, totals and the checkout API.", after, StringComparison.Ordinal);
    }
}
