using Parley.Configuration;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// One repository, more than one project.
/// </summary>
/// <remarks>
/// <para>
/// <b>SPEC.md §4.1 has always permitted this and the tooling forbade it.</b> The protocol has no
/// concept of a project, membership is which endpoint an agent connects to, and names collide freely
/// across projects — two registries on one machine may each hold a row for one directory and neither
/// broker can see the other's file. What forbade it was a fixed key here: one key holds one entry, so
/// one repository held one identity, and the refusal that enforced it was written against
/// <i>re-pointing</i>, which is a different thing that is still refused.
/// </para>
/// <para>
/// The cost was paid by exactly the repositories worth asking. Brokers do not federate, so a
/// repository several product lines depend on had to pick one set of peers and be unreachable to the
/// rest.
/// </para>
/// <para>
/// <b>Every case here asserts on the whole file, not on the entry it added.</b> The failure this
/// feature could introduce is not "the second enrolment did not work" — it is "the second enrolment
/// worked and quietly changed the first", which only an assertion about what was left alone can catch.
/// </para>
/// </remarks>
public sealed class MultiProjectEnrolmentTests
{
    private const string Workspace = "/srv/checkouts/inventory";

    private static EnrollmentEntry Entry(string agent, string owns) =>
        new(Workspace, agent, owns, false);

    private static JsonObject Servers(string launch) =>
        (JsonObject)JsonNode.Parse(launch)!["mcpServers"]!;

    private const string InAlpha = """
        {
          "mcpServers": {
            "parley-alpha": {
              "command": "parley-mcp",
              "env": {
                "PARLEY_AGENT": "inventory",
                "PARLEY_PROJECT": "alpha",
                "PARLEY_OWNS": "Stock levels and reservations. Not pricing.",
                "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-/srv/checkouts/inventory}"
              }
            },
            "some-other-server": { "command": "whatever" }
          },
          "parleyAgentId": "3f2b86d1-9c47-4a05-b8e3-51d0c7a29e64"
        }
        """;

    [Fact]
    public void A_repository_already_in_one_project_may_be_enrolled_in_another()
    {
        var plan = EnrollmentPlan.Describe(
            Entry("stock", "What is on hand, and what is promised."),
            InAlpha,
            null,
            false,
            project: "beta");

        var servers = Servers(plan.LaunchConfiguration!);

        Assert.Equal(
            ["parley-alpha", "parley-beta", "some-other-server"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void The_first_project_s_entry_is_left_exactly_as_it_was()
    {
        // The acceptance scenario, asserted the only way that means anything: the node itself, not a
        // re-read of the fields this happens to know about.
        var plan = EnrollmentPlan.Describe(
            Entry("stock", "What is on hand, and what is promised."),
            InAlpha,
            null,
            false,
            project: "beta");

        Assert.True(JsonNode.DeepEquals(
            Servers(InAlpha)["parley-alpha"],
            Servers(plan.LaunchConfiguration!)["parley-alpha"]));
    }

    [Fact]
    public void Each_project_gets_its_own_name_and_its_own_owns_line()
    {
        // Not a side effect: the same repository is worth asking about different things by different
        // rosters, and nothing forces the two entries to agree.
        var plan = EnrollmentPlan.Describe(
            Entry("stock", "What is on hand, and what is promised."),
            InAlpha,
            null,
            false,
            project: "beta");

        var servers = Servers(plan.LaunchConfiguration!);
        var beta = servers["parley-beta"]!["env"]!;

        Assert.Equal("stock", beta["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("beta", beta["PARLEY_PROJECT"]!.GetValue<string>());
        Assert.Equal(
            "inventory",
            servers["parley-alpha"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
    }

    [Fact]
    public void Another_server_survives_the_second_enrolment()
    {
        var plan = EnrollmentPlan.Describe(
            Entry("stock", "What is on hand."),
            InAlpha,
            null,
            false,
            project: "beta");

        Assert.True(JsonNode.DeepEquals(
            Servers(InAlpha)["some-other-server"],
            Servers(plan.LaunchConfiguration!)["some-other-server"]));
    }

    [Fact]
    public void Enrolling_again_into_a_project_it_already_answers_in_changes_nothing()
    {
        // FR-015. The run must not report a change it did not make.
        //
        // **The fixture carries an identity because 018 made that part of "already correct".** A
        // repository that declares none is genuinely changed by an enrolment — it gains one — so the
        // file below would be rewritten and the run would be right to say so. That case is its own
        // test rather than this one's silence.
        var plan = EnrollmentPlan.Describe(
            Entry("inventory", "Stock levels and reservations. Not pricing."),
            InAlpha,
            null,
            false,
            project: "alpha");

        Assert.Null(plan.LaunchConfiguration);
        Assert.Contains(plan.Notes, note => note.Contains("already correct", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repository_that_declares_no_identity_gains_one_and_the_run_says_so()
    {
        // The other half of the test above, and the one enrolment actually performs on every
        // repository enrolled before 018. The identity is what the catalogue is keyed by, so a
        // checkout without one is startable by nothing; gaining one is a change to somebody's file and
        // is reported like every other.
        // Taken out through the parser rather than by string surgery, because the member's line ending
        // is whatever this file has and a literal would match on one platform and not the other.
        var stripped = JsonNode.Parse(InAlpha)!.AsObject();

        _ = stripped.Remove(LaunchConfiguration.IdentityMember);

        var without = stripped.ToJsonString();

        var plan = EnrollmentPlan.Describe(
            Entry("inventory", "Stock levels and reservations. Not pricing."),
            without,
            null,
            false,
            project: "alpha");

        Assert.NotNull(plan.LaunchConfiguration);
        Assert.True(plan.Identity.Known);
        Assert.Contains(
            plan.Notes,
            note => note.Contains(LaunchConfiguration.IdentityMember, StringComparison.Ordinal));

        // And it is the one written into the file, not a second one minted on the way out.
        Assert.Equal(
            plan.Identity.ToString(),
            JsonNode.Parse(plan.LaunchConfiguration!)![LaunchConfiguration.IdentityMember]!
                .GetValue<string>());
    }

    [Fact]
    public void A_legacy_entry_is_updated_where_it_stands_and_never_renamed()
    {
        // User Story 2's second acceptance scenario. The key is a component of every tool name the
        // agent behind it already sees, so a rename edits a working agent's vocabulary to achieve
        // nothing at all.
        const string Legacy = """
            {
              "mcpServers": {
                "parley": {
                  "command": "parley-mcp",
                  "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" }
                }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(
            Entry("orders", "Carts and totals."),
            Legacy,
            null,
            false,
            project: "alpha");

        var keys = Servers(plan.LaunchConfiguration!).Select(pair => pair.Key).ToArray();

        Assert.Equal(["parley"], keys);
    }

    [Fact]
    public void A_legacy_entry_pinned_elsewhere_gains_a_neighbour_rather_than_being_moved()
    {
        // User Story 2's third acceptance scenario, and the case the old refusal caught. Re-pointing
        // is still refused; what is admitted is a second entry beside the first.
        const string Legacy = """
            {
              "mcpServers": {
                "parley": {
                  "command": "parley-mcp",
                  "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" }
                }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(
            Entry("orders", "Carts and totals."),
            Legacy,
            null,
            false,
            project: "beta");

        var servers = Servers(plan.LaunchConfiguration!);

        Assert.Equal(["parley", "parley-beta"], servers.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(JsonNode.DeepEquals(Servers(Legacy)["parley"], servers["parley"]));
    }

    [Fact]
    public void A_repository_may_answer_in_three()
    {
        const string InTwo = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "stock", "PARLEY_PROJECT": "beta" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(
            Entry("warehouse", "Where things physically are."),
            InTwo,
            null,
            false,
            project: "gamma");

        Assert.Equal(3, Servers(plan.LaunchConfiguration!).Count);
    }

    [Fact]
    public void An_entry_whose_key_and_environment_disagree_is_refused_rather_than_written_over()
    {
        // A hand edit, which is the ordinary state of this file. The key says alpha and the running
        // adapter would reach beta: writing here re-points beta's agent silently, and skipping the key
        // writes over what is there. Neither may happen without somebody looking at it.
        const string Confused = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "beta" } }
              }
            }
            """;

        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry("inventory", "Stock."), Confused, null, false, project: "alpha"));

        Assert.Contains("alpha", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("beta", refusal.Message, StringComparison.Ordinal);
    }
}
