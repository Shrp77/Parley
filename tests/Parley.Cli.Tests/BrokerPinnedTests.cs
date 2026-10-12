using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A repository pinned to a broker address rather than to a project name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The old refusal was right about what it could see and is now able to see more.</b> It read:
/// <i>"this cannot tell whether that address is the project it is being asked to join, and guessing
/// wrong in the permissive direction is the move that loses an agent"</i> — true when the only evidence
/// was the address itself. The machine's manifest knows which project holds which endpoint, so the
/// question is decidable rather than a guess, and the answer differs in each direction.
/// </para>
/// <para>
/// <b>The map is passed in rather than read here, and that is deliberate.</b> <c>Describe</c> takes
/// file contents as strings and returns file contents as strings, which is why it is the most heavily
/// tested type in this project; reaching a manifest from inside it would give every one of those tests
/// a filesystem.
/// </para>
/// </remarks>
public sealed class BrokerPinnedTests
{
    private static EnrollmentEntry Entry() =>
        new("/srv/checkouts/orders", "orders", "Carts and totals.", false);

    private const string PinnedToPort7777 = """
        {
          "mcpServers": {
            "parley": { "command": "parley-mcp", "env": {
              "PARLEY_AGENT": "orders", "PARLEY_BROKER": "127.0.0.1:7777" } }
          }
        }
        """;

    private static readonly Dictionary<string, string> Endpoints = new(StringComparer.Ordinal)
    {
        ["acme"] = "127.0.0.1:7777",
        ["beta"] = "127.0.0.1:7778",
    };

    [Fact]
    public void An_address_that_is_this_project_is_refused_as_a_duplicate()
    {
        // Already this project's agent, spelled the old way. Adding a second entry would give one
        // repository two identities in one roster, which nothing else in the deployment can tell apart.
        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(
                Entry(), PinnedToPort7777, null, false, project: "acme", endpoints: Endpoints));

        Assert.Contains("127.0.0.1:7777", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("acme", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_that_is_another_project_is_an_ordinary_second_enrolment()
    {
        // This is a repository in two projects, one of them spelled the old way. Nothing is lost by
        // admitting it, and the entry that is there is untouched.
        var plan = EnrollmentPlan.Describe(
            Entry(), PinnedToPort7777, null, false, project: "beta", endpoints: Endpoints);

        var servers = (JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!;

        Assert.Equal(["parley", "parley-beta"], servers.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            "127.0.0.1:7777",
            servers["parley"]!["env"]!["PARLEY_BROKER"]!.GetValue<string>());
    }

    [Fact]
    public void An_address_no_project_holds_is_refused_and_named()
    {
        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(
                Entry(), PinnedToPort7777, null, false, project: "beta",
                endpoints: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["beta"] = "127.0.0.1:7778",
                }));

        Assert.Contains("127.0.0.1:7777", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_manifest_at_all_it_is_refused_exactly_as_it_was_before()
    {
        // The behaviour that shipped before this feature, kept for the case where the evidence that
        // made it decidable is absent. Failing open here is what loses an agent from a roster.
        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry(), PinnedToPort7777, null, false, project: "acme"));

        Assert.Contains("127.0.0.1:7777", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_on_an_entry_this_enrolment_is_not_landing_on_is_not_consulted()
    {
        // The pin belongs to alpha's entry; this enrolment is adding beta's. Whether alpha's address
        // is recognisable says nothing about whether beta may be added, and treating it as an obstacle
        // would make one stale pin block every future project.
        const string PinnedUnderItsOwnKey = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha",
                  "PARLEY_BROKER": "127.0.0.1:9999" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(
            Entry(), PinnedUnderItsOwnKey, null, false, project: "beta", endpoints: Endpoints);

        Assert.NotNull(plan.LaunchConfiguration);
    }
}
