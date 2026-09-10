using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A key this tooling would write at, held by an entry it did not write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three silent replacements of somebody's MCP configuration are recorded in this codebase's
/// comments, and none of them was malice.</b> Each was a cast or an index that treated a shape it did
/// not understand as a shape it could overwrite, and each reported a successful enrolment. Deriving the
/// key from a project name creates a fourth opportunity — a repository may perfectly reasonably
/// already run a server called <c>parley-alpha</c> — so it is closed before it is opened.
/// </para>
/// <para>
/// <b>"Ours" is broader than "names an agent", and that distinction is the whole test.</b> An entry
/// carrying <c>PARLEY_PROJECT</c> or <c>PARLEY_BROKER</c> and no <c>PARLEY_AGENT</c> is this tooling's
/// half-written entry, not a stranger's; refusing it would refuse the ordinary re-enrolment of a
/// repository whose name was cleared by hand. An entry carrying no parley variable at all and some
/// other command is somebody else's.
/// </para>
/// </remarks>
public sealed class StrangerKeyTests
{
    private static EnrollmentEntry Entry() =>
        new("/srv/checkouts/orders", "orders", "Carts and totals.", false) { OwnsApproved = true };

    [Fact]
    public void A_hand_written_server_at_our_key_is_refused_and_named()
    {
        const string Existing = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "some-other-thing", "args": ["--serve"] }
              }
            }
            """;

        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry(), Existing, null, false, project: "alpha"));

        Assert.Contains("parley-alpha", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stranger_is_still_there_afterwards()
    {
        // Belt and braces on the refusal: a plan that threw after mutating its copy would still have
        // reported the right message, and the caller writes what the plan hands back.
        const string Existing = """
            { "mcpServers": { "parley-alpha": { "command": "some-other-thing" } } }
            """;

        try
        {
            EnrollmentPlan.Describe(Entry(), Existing, null, false, project: "alpha");
        }
        catch (InvalidDataException)
        {
            // Expected. What matters is what was not written.
        }

        var servers = (JsonObject)JsonNode.Parse(Existing)!["mcpServers"]!;

        Assert.Equal("some-other-thing", servers["parley-alpha"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Our_own_half_written_entry_is_not_a_stranger()
    {
        // Carries a parley variable and no agent — the shape a person leaves behind by clearing a
        // name in an editor. Refusing this would refuse the repair.
        const string Existing = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_PROJECT": "alpha" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), Existing, null, false, project: "alpha");

        Assert.NotNull(plan.LaunchConfiguration);
    }

    [Fact]
    public void An_entry_that_is_only_our_command_is_not_a_stranger_either()
    {
        const string Existing = """
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp" } } }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), Existing, null, false, project: "alpha");

        Assert.NotNull(plan.LaunchConfiguration);
    }

    [Fact]
    public void A_stranger_under_a_key_we_would_never_write_is_none_of_our_business()
    {
        const string Existing = """
            {
              "mcpServers": {
                "parley-Alpha": { "command": "some-other-thing" },
                "parleyx": { "command": "some-other-thing" }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), Existing, null, false, project: "alpha");
        var servers = (JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!;

        Assert.Equal(3, servers.Count);
        Assert.Equal("some-other-thing", servers["parley-Alpha"]!["command"]!.GetValue<string>());
    }
}
