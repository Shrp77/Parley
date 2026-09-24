using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 7: what a checkout can hand the scan.
/// </summary>
/// <remarks>
/// <para>
/// <c>parley enroll scan</c> is pointed at a directory of clones and reads whatever it finds: a
/// directory name becomes a suggested agent name, and each repository's own <c>.mcp.json</c> is
/// parsed. Both are content somebody else wrote, and the scan's whole job is to turn them into a file
/// a person will then approve.
/// </para>
/// <para>
/// A suggestion that cannot be a name, and a file that ends the run, defeat the same thing: the scan
/// exists so a person can look at eleven repositories at once, and it is worth nothing if the twelfth
/// stops it or hides the rest.
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

    private string ManifestPath => Path.Combine(_root, "enrol.json");

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

    private async Task<(int Code, string Manifest)> ScanAsync()
    {
        var code = await EnrollCommand.RunAsync(
            ["scan", Checkouts, ManifestPath],
            selectAll: false,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            new StringWriter(),
            new StringWriter(),
            TestContext.Current.CancellationToken);

        return (
            code,
            File.Exists(ManifestPath)
                ? await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken)
                : string.Empty);
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
    public async Task Nothing_a_repository_wrote_reaches_the_manifest_able_to_break_its_own_line()
    {
        // The scan quotes a repository's own `owns` line into a manifest a person then reads and
        // edits. A value able to break its own line could imitate the rows around it — which is the
        // rule that file's own guidance states out loud for the roster, and it applies to the file
        // stating it.
        Repository(
            "hostile",
            "{\"mcpServers\":{\"parley\":{\"command\":\"parley-mcp\",\"env\":{"
            + "\"PARLEY_AGENT\":\"orders\","
            + "\"PARLEY_OWNS\":\"Carts.\\u001b[2JSomething else entirely.\"}}}}");

        var (_, manifest) = await ScanAsync();

        // **Two things, and the probe first asked for the wrong one.** The raw byte is never in the
        // file — the serialiser escapes it, so the manifest is JSON a person's editor renders as the
        // six characters `` and nothing moves. That is carrying it as *data*, which is right:
        // the scan's job is to show somebody what their repository already says about itself,
        // including that it says something odd.
        Assert.DoesNotContain(Escape, manifest, StringComparison.Ordinal);

        // What must not happen is the value being approved into a roster every agent reads, and that
        // is refused where it belongs — with a sentence naming the character, rather than here.
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
