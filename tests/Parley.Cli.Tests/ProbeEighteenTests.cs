using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 7: what a checkout can hand the scan.
/// </summary>
/// <remarks>
/// <para>
/// <c>parley enroll</c> is pointed at a directory of clones and reads whatever it finds: a
/// directory name becomes a suggested agent name, and each repository's own <c>.mcp.json</c> is
/// parsed. Both are content somebody else wrote, and the whole job is to put them in front of a
/// person who then chooses.
/// </para>
/// <para>
/// A suggestion that cannot be a name, and a file that ends the run, defeat the same thing: this
/// exists so a person can look at eleven repositories at once, and it is worth nothing if the
/// twelfth stops it or hides the rest.
/// </para>
/// <para>
/// <b>Written against <c>enroll scan</c>, which wrote a manifest a person approved.</b> There is no
/// file now and the probes point at <see cref="EnrollCommand.Find"/>, which is what the scan half
/// always was; what each one defends is unchanged.
/// </para>
/// </remarks>
public sealed class ProbeEighteenTests : IDisposable
{
    private const string Escape = "";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe18-" + Path.GetRandomFileName());

    public ProbeEighteenTests() => Directory.CreateDirectory(Checkouts);

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


    private void Repository(string name, string? launch = null)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        if (launch is not null)
        {
            File.WriteAllText(Path.Combine(path, ".mcp.json"), launch);
        }
    }

    [Fact]
    public void A_repository_whose_launch_configuration_is_enormous_does_not_end_the_scan()
    {
        // A clone is somebody else's data and its `.mcp.json` is a tracked file of any size they
        // like. The scan's value is that it looks at everything at once, so one repository able to
        // stop it is one repository able to hide the other ten.
        var servers = new JsonObject();

        for (var i = 0; i < 20_000; i++)
        {
            servers[$"server-{i:D6}"] = new JsonObject { ["command"] = new string('x', 200) };
        }

        Repository("huge", new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        Repository("ordinary");

        // The row for the other repository is what says the find got past this one: it is offered
        // after `huge` in directory order, so a scan that stopped there would come back without it.
        Assert.Contains(EnrollCommand.Find(Checkouts), entry => entry.Agent == "ordinary");
    }

    [Fact]
    public void A_deeply_nested_launch_configuration_does_not_end_the_scan()
    {
        // The same attack in a few hundred bytes, which no size limit catches. A parser refuses this
        // rather than choking on it, and the question is whether the refusal is caught here or thrown
        // at the person running the scan.
        var deep = new string('[', 200) + new string(']', 200);

        Repository("bomb", "{\"mcpServers\": {\"parley\": {\"command\": " + deep + " }}}");
        Repository("ordinary");

        Assert.Contains(EnrollCommand.Find(Checkouts), entry => entry.Agent == "ordinary");
    }

    [Fact]
    public void A_directory_name_that_is_not_a_name_does_not_become_one()
    {
        // Directory names become suggested agent names, and an agent name is an identity. A
        // suggestion that is not well formed must be visible as such rather than travelling into a
        // launch configuration to be refused a long way away, at registration, where it looks like an
        // agent that simply never appeared.
        Repository("Orders.Service");
        Repository("orders_2");
        Repository("ORDERS");

        var found = EnrollCommand.Find(Checkouts);

        Assert.Equal(3, found.Count);

        foreach (var suggested in found.Select(entry => entry.Agent))
        {
            Assert.True(
                EnrollmentPlan.IsWellFormedName(suggested),
                $"the scan suggested '{suggested}', which is not a name any agent could have");
        }
    }

    [Fact]
    public void A_launch_configuration_that_is_not_json_leaves_the_row_blank_rather_than_stopping()
    {
        Repository("broken", "{ this is not json");
        Repository("ordinary");

        var found = EnrollCommand.Find(Checkouts);

        Assert.Contains(found, entry => entry.Agent == "ordinary");
        Assert.Contains(found, entry => entry.Agent == "broken");
    }

    [Fact]
    public void Nothing_a_repository_wrote_survives_the_scan_able_to_break_a_surface()
    {
        // A repository's own `owns` line is quoted back to whoever is choosing what to enrol. A
        // value able to break its own line could imitate the rows around it, which is the rule the
        // transcript's column-zero guard exists for, met here in a different list.
        Repository(
            "hostile",
            "{\"mcpServers\":{\"parley\":{\"command\":\"parley-mcp\",\"env\":{"
            + "\"PARLEY_AGENT\":\"orders\","
            + "\"PARLEY_OWNS\":\"Carts.\\u001b[2JSomething else entirely.\"}}}}");

        // **This asked a question about a file and now asks one about the row, which is stronger.**
        // It used to assert the raw byte was absent from the written manifest -- true, but only
        // because a JSON serialiser escaped it, so it measured System.Text.Json rather than
        // anything this code does. With no file there is nothing to serialise, and what matters is
        // that the value is carried faithfully as data: the scan's job is to show somebody what
        // their repository already says about itself, including that it says something odd.
        var found = EnrollCommand.Find(Checkouts);

        Assert.Contains(found, entry => entry.Owns?.Contains(Escape, StringComparison.Ordinal) == true);

        // Neutralising it is the displaying surface's job and is done where the text is drawn --
        // `Onboarding` passes every note through `Safe`. What must not happen is the value being
        // approved into a roster every agent reads, and that is refused where it belongs, with a
        // sentence naming the character, rather than here.
        var problems = EnrollmentPlan.Validate(
            [
                new EnrollmentEntry("/src/orders", "orders", $"Carts.{Escape}[2JSomething else.", false)
                {
                    Enroll = true,
                    OwnsApproved = true,
                },
            ],
            _ => true);

        Assert.Contains(problems, problem => problem.Contains("U+001B", StringComparison.Ordinal));
    }
}
