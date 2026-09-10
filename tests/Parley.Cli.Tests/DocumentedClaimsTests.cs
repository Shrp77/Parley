using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The claims this feature's documentation makes, as checks.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 3. The <c>remarks</c> in this feature carry the reasoning, which is the
/// house style and the reason the code is readable — and several of them are not reasoning but
/// <i>assertions of fact</i>: a legacy key is never renamed, withdrawing never touches the standing
/// instructions, an entry at our key that carries no variable is still ours if it runs the adapter.
/// </para>
/// <para>
/// A sentence like that is a promise to whoever reads it while deciding whether this is safe to point
/// at a checkout, and nothing was checking any of them. This gate has already found two comments that
/// had stopped being true — one naming a removed flag, one arguing for behaviour that had been the
/// wrong behaviour all along. Those were found by reading; these are checked.
/// </para>
/// </remarks>
public sealed class DocumentedClaimsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-claims-" + Path.GetRandomFileName());

    public DocumentedClaimsTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string Instructions => Path.Combine(Workspace, "CLAUDE.md");

    private Task<Enrolment.Outcome> EnrolAsync(string? project) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            project ?? "alpha",
            Path.Combine(_root, "node.json"),
            Instructions,
            TestContext.Current.CancellationToken);

    private async Task<JsonObject> ServersAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

    [Fact]
    public async Task A_legacy_entry_is_read_and_never_renamed_in_place()
    {
        // `LaunchKey`: "Reading it is permanent; it is never renamed in place, because the key is a
        // component of every tool name the agent behind it already sees." Renaming would edit an
        // agent's vocabulary — `mcp__parley__parley_ask` becomes `mcp__parley_alpha__parley_ask` — to
        // change nothing, which is the one cost this feature refused to pay.
        await File.WriteAllTextAsync(
            LaunchFile,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["parley"] = new JsonObject
                    {
                        ["command"] = EnrollmentPlan.AdapterCommand,
                        ["env"] = new JsonObject { ["PARLEY_AGENT"] = "orders" },
                    },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await EnrolAsync("alpha");

        var servers = await ServersAsync();

        // Re-pointed in place under the key it already had, rather than renamed to `parley-alpha`.
        Assert.True(servers.ContainsKey("parley"), "the legacy key was renamed");
        Assert.Equal("alpha", servers["parley"]!["env"]![EnrollmentPlan.ProjectVariable]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawing_never_touches_the_standing_instructions()
    {
        // `Enrolment`'s class doc: "withdrawing never touches it at all ... and says so rather than
        // leaving somebody to wonder". Both halves — the file byte for byte, and the sentence.
        await EnrolAsync("alpha");

        var before = await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "alpha", Path.Combine(_root, "node.json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            before,
            await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken));

        Assert.Contains(
            outcome.Notes,
            note => note.Contains("instructions", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("untouched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_entry_running_the_adapter_with_no_variables_is_still_ours()
    {
        // `EnrollmentPlan.AdapterCommand`: "an entry at a key this would write at, carrying no parley
        // variable at all, is still ours if it runs the adapter. A person who cleared an environment
        // by hand has not handed their entry to somebody else." The alternative is FR-009 refusing to
        // touch an entry this program wrote.
        await File.WriteAllTextAsync(
            LaunchFile,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["parley-alpha"] = new JsonObject { ["command"] = EnrollmentPlan.AdapterCommand },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await EnrolAsync("alpha");

        Assert.Equal(
            "orders",
            (await ServersAsync())["parley-alpha"]!["env"]![EnrollmentPlan.AgentVariable]!.GetValue<string>());
    }

    [Fact]
    public async Task A_peers_section_that_exists_is_left_exactly_as_it_is()
    {
        // `Enrolment`'s class doc again: a section that exists is left alone, because "nothing here
        // can tell a line the project added from a line a tool did". The control for the claim above
        // it — the file is written when there is no section, so "untouched" has to mean the case that
        // has one.
        var mine = "# Orders\n\n## Peers\n\n- billing: invoices. Ask it about tax.\n";

        await File.WriteAllTextAsync(Instructions, mine, TestContext.Current.CancellationToken);

        await EnrolAsync("alpha");

        Assert.Equal(
            mine,
            await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken));
    }
}
