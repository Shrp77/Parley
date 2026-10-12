using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 4: whether what the operator is told about a repository is true.
/// </summary>
/// <remarks>
/// <para>
/// Runs 1 to 3 attacked what gets written and where. This attacks what gets <i>said</i>. Every
/// decision a person makes here — enrol it again, withdraw it, leave it alone — is made from a line
/// this tooling printed about a file it read, and a line that is subtly wrong is worse than a refusal:
/// a refusal stops somebody, a wrong sentence sends them on.
/// </para>
/// <para>
/// The cases that matter are the ones where two readings exist and only one is the truth about what
/// the agent will actually do.
/// </para>
/// </remarks>
public sealed class ProbeFifteenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe15-" + Path.GetRandomFileName());

    public ProbeFifteenTests() => Directory.CreateDirectory(_root);

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

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private static JsonObject Servers(string json) =>
        (JsonObject)JsonNode.Parse(json)!["mcpServers"]!;

    [Fact]
    public void An_entry_whose_key_and_environment_disagree_reports_the_broker_it_will_reach()
    {
        // Two readings, one truth. The key namespaces the client's tools; the environment decides
        // which broker the agent connects to. A reader that answered with the key would tell somebody
        // their repository answers in `alpha` while every message it sends goes to `beta`.
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta" } } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Equal("beta", entry.Project);
        Assert.True(entry.Mismatched, "the disagreement itself must reach the caller");
    }

    [Fact]
    public async Task A_withdrawal_counts_what_is_left_exactly()
    {
        // The person decides whether the repository is finished with from this line. Counting the
        // removed entry, or counting another tool's server as one of ours, both send somebody to look
        // at the wrong thing.
        var workspace = Workspace("orders");

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta" } },
                "parley-gamma": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "gamma" } },
                "other-one": { "command": "x" },
                "other-two": { "command": "y" }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            workspace, "orders", "beta", null, TestContext.Current.CancellationToken);

        Assert.Contains(
            outcome.Notes,
            note => note.Contains("2 parley", StringComparison.Ordinal)
                    && note.Contains("2 other server", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plan_for_a_repository_in_another_project_does_not_claim_it_will_change_it()
    {
        // The note a person reads before deciding whether the run is safe. It must describe the entry
        // that is being added, and must not describe the one that is not being touched.
        var plan = EnrollmentPlan.Describe(
            new EnrollmentEntry("/src/orders", "orders", "Carts.", Unattended: false)
            {
                Enroll = true,
            },
            """
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } } } }
            """,
            null,
            false,
            project: "beta");

        var notes = string.Join(" ", plan.Notes);

        Assert.Contains("parley-beta", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("update parley-alpha", notes, StringComparison.Ordinal);

        // And the file bears it out, which is the half a note cannot be trusted for.
        Assert.Equal(
            ["parley-alpha", "parley-beta"],
            Servers(plan.LaunchConfiguration!).Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_hard_link_is_not_caught_by_the_link_check_and_this_is_the_record_of_it()
    {
        // **A recorded limit rather than a finding.** `FileInfo.LinkTarget` reports symbolic links and
        // reparse points; a hard link is indistinguishable from the file itself, because it *is* the
        // file — there is no target to report and no portable way to ask.
        //
        // It is not reachable by the route 2.3 closed: git stores file contents, and a checkout
        // creates ordinary files, so a repository cannot ship a hard link the way it can ship a
        // symlink. Reaching this needs write access to the checkout already, which is a different
        // threat with different remedies.
        //
        // Written down because the next person to read the link check will wonder, and because a
        // limit nobody recorded is a limit somebody re-discovers as a surprise.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.json");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace, "probe-link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return; // Without symlink privilege the comparison below says nothing.
        }

        // The check sees a symlink...
        Assert.NotNull(new FileInfo(Path.Combine(workspace, "probe-link")).LinkTarget);

        // ...and an ordinary file has nothing to see, which is the same answer it would give a hard
        // link. The limit is in the platform, not in the caller.
        Assert.Null(new FileInfo(outside).LinkTarget);
    }
}
