using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 21: two repositories, one role.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnrollmentPlan.Validate</c> refuses two rows of one manifest sharing an agent name, and says why
/// in the strongest terms this codebase uses: identity is the one thing Parley makes unforgeable, it
/// does that by taking the name from the launch environment, and two servers claiming one name means
/// either may collect the other's mail with neither side able to tell.
/// </para>
/// <para>
/// That rule is scoped to one manifest. The terminal enrols one repository at a time, and this feature
/// exists because a machine now holds several projects — so the same collision assembled across two
/// calls is the ordinary way to reach it, and no manifest ever contains both rows.
/// </para>
/// </remarks>
public sealed class ProbeThirtyTwoTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe32-" + Path.GetRandomFileName());

    public ProbeThirtyTwoTests()
    {
        Workspace("first");
        Workspace("second");
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

    private string CataloguePath => Path.Combine(_root, "node.json");

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private Task<Enrolment.Outcome> EnrolAsync(string workspace, string agent, bool unattended = true) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(workspace, agent, "Carts and totals.", unattended)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "alpha",
            CataloguePath,
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    private async Task<JsonObject> AgentsAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!["agents"]!;

    [Fact]
    public async Task A_second_repository_taking_a_role_the_first_holds_is_said_out_loud()
    {
        // The collision the manifest rule exists to refuse, assembled one call at a time. Whatever is
        // decided about the catalogue, the person doing the second enrolment has just given a second
        // repository a name the deployment already uses, and nothing else will tell them.
        await EnrolAsync(Workspace("first"), "orders");

        var outcome = await EnrolAsync(Workspace("second"), "orders");

        Assert.Contains(
            outcome.Notes,
            note => note.Contains(Workspace("first"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_catalogue_does_not_end_up_pointing_at_both_or_neither()
    {
        // Whichever repository the role ends up naming, it must name exactly one: the catalogue is
        // where a node is told which directory to start a process in, and this is the file that
        // decides it.
        await EnrolAsync(Workspace("first"), "orders");
        await EnrolAsync(Workspace("second"), "orders");

        var listed = Assert.Single(await AgentsAsync());

        Assert.Equal("orders", listed.Key);
        Assert.Contains(
            listed.Value!["workspace"]!.GetValue<string>(),
            (string[])[Workspace("first"), Workspace("second")]);
    }

    [Fact]
    public async Task Re_enrolling_the_same_repository_under_the_same_role_says_nothing_alarming()
    {
        // The control, and the case that must not be caught by whatever catches the one above:
        // enrolling a repository twice is the ordinary repair, and the catalogue line it finds is
        // its own.
        var workspace = Workspace("first");

        await EnrolAsync(workspace, "orders");

        var outcome = await EnrolAsync(workspace, "orders");

        Assert.DoesNotContain(
            outcome.Notes,
            note => note.Contains(Workspace("second"), StringComparison.Ordinal));

        Assert.Equal(
            workspace,
            (await AgentsAsync())["orders"]!["workspace"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_workspace_this_quotes_out_of_the_catalogue_is_flattened()
    {
        // **2.8 enumerated the sentences built from a file and missed this one.** It needs an
        // existing catalogue entry holding a hostile workspace, which that probe did not construct —
        // so the enumeration was of the call sites it could see rather than of the messages. Both
        // branches quote it now and both are driven here.
        // Built rather than written as a literal, so the escaping is the serialiser's problem: what
        // has to reach the catalogue is a workspace whose *value* carries these, not a file holding
        // raw control bytes, which is not JSON at all.
        var hostile = "/srv/" + (char)0x1B + "[2J" + (char)0x202E + "orders";

        await File.WriteAllTextAsync(
            CataloguePath,
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    ["orders"] = new JsonObject
                    {
                        ["command"] = "claude",
                        ["workspace"] = hostile,
                    },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        foreach (var unattended in (bool[])[true, false])
        {
            var outcome = await EnrolAsync(Workspace("second"), "orders", unattended);

            foreach (var character in string.Join(" | ", outcome.Notes))
            {
                Assert.False(
                    char.IsControl(character),
                    $"a note carried U+{(int)character:X4}");
            }

            Assert.DoesNotContain(
                "‮", string.Join(" | ", outcome.Notes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_attended_second_claim_is_still_a_second_claim()
    {
        // The launch configuration is where the name actually lives, so a repository that is never
        // catalogued still claims the name the moment somebody opens it in an editor. A rule that
        // only looked at the catalogue would miss the half that does not need a node at all.
        await EnrolAsync(Workspace("first"), "orders");

        var outcome = await EnrolAsync(Workspace("second"), "orders", unattended: false);

        Assert.Contains(
            outcome.Notes,
            note => note.Contains(Workspace("first"), StringComparison.Ordinal));
    }
}
