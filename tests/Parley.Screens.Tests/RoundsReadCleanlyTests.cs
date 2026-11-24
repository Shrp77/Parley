using Xunit;

namespace Parley.Screens.Tests;

/// <summary>That every round under tools/Parley.Screens/rounds reads cleanly, and that what a round gets wrong is said at load.</summary>
/// <remarks>
/// A round is only run by hand, when screenshots are wanted, so a change to the tool's formats would otherwise orphan
/// the last round silently until somebody next needed it. This is the <c>check</c> command, run on every push.
/// </remarks>
[Unit]
public sealed class RoundsReadCleanlyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("parley-screens-round-").FullName;

    public static TheoryData<string> Rounds()
    {
        var rounds = new TheoryData<string>();

        foreach (var round in Directory.GetDirectories(Path.Combine(Repository(), "tools", "Parley.Screens", "rounds")))
        {
            rounds.Add(Path.GetFileName(round));
        }

        return rounds;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [MemberData(nameof(Rounds))]
    public void Every_round_reads_and_every_shot_names_only_what_its_scenario_makes(string round)
    {
        var folder = Path.Combine(Repository(), "tools", "Parley.Screens", "rounds", round);

        var scenario = Scenario.Load(Path.Combine(folder, "scenario.json"));
        var shots = ShotList.Load(Path.Combine(folder, "shots.json"));

        Assert.Empty(shots.Against(scenario));
    }

    [Fact]
    public void A_missing_member_is_said_rather_than_crashing()
    {
        var problem = Assert.Throws<InvalidDataException>(() => LoadScenario("""{ "project": "shop", "machine": "devbox", "agents": [] }"""));

        Assert.Contains("steps", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposal_label_cannot_stand_for_an_exchange()
    {
        var problem = Assert.Throws<InvalidDataException>(() => LoadScenario("""
            { "project": "shop", "machine": "devbox",
              "agents": [ { "role": "orders", "owns": "Orders.", "started": "node" } ],
              "steps": [
                { "do": "propose", "by": "orders", "title": "Plan", "items": [ "Do it" ], "label": "plan" },
                { "do": "clarify", "from": "orders", "exchange": "plan", "text": "Which?" } ] }
            """));

        Assert.Contains("exchange 'plan' is no earlier request's label", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_outside_the_round_is_refused()
    {
        var problem = Assert.Throws<InvalidDataException>(() => LoadScenario("""
            { "project": "shop", "machine": "devbox",
              "agents": [ { "role": "orders", "owns": "Orders.", "started": "node" } ],
              "steps": [ { "do": "place", "by": "orders", "name": "x.md", "file": "../../secret.md" } ] }
            """));

        Assert.Contains("outside the round's folder", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_outside_the_grammar_is_refused()
    {
        var problem = Assert.Throws<InvalidDataException>(() => LoadScenario("""
            { "project": "Shop Floor", "machine": "devbox", "agents": [], "steps": [] }
            """));

        Assert.Contains("project 'Shop Floor'", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drive_relative_shot_name_is_refused()
    {
        var path = Path.Combine(_folder, "shots.json");
        File.WriteAllText(path, """
            { "theme": "dark", "scale": 1, "maximumBytes": 0,
              "shots": [ { "file": "C:x.png", "width": 800, "height": 600, "path": "/Inbox" } ] }
            """);

        var problem = Assert.Throws<InvalidDataException>(() => ShotList.Load(path));

        Assert.Contains("no folder or drive", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shot_may_take_its_own_theme_and_no_other_word()
    {
        var path = Path.Combine(_folder, "shots.json");
        File.WriteAllText(path, """
            { "theme": "light", "scale": 1, "maximumBytes": 0,
              "shots": [ { "file": "a.png", "width": 800, "height": 600, "path": "/Inbox", "theme": "dark" },
                         { "file": "b.png", "width": 800, "height": 600, "path": "/Inbox" } ] }
            """);

        var list = ShotList.Load(path);

        Assert.Equal("dark", list.Shots[0].ThemeIn(list));
        Assert.Equal("light", list.Shots[1].ThemeIn(list));

        File.WriteAllText(path, """
            { "theme": "light", "scale": 1, "maximumBytes": 0,
              "shots": [ { "file": "a.png", "width": 800, "height": 600, "path": "/Inbox", "theme": "dusk" } ] }
            """);

        Assert.Contains("theme must be dark or light", Assert.Throws<InvalidDataException>(() => ShotList.Load(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_written_twice_is_refused()
    {
        var path = Path.Combine(_folder, "shots.json");
        File.WriteAllText(path, """
            { "theme": "dark", "scale": 1, "maximumBytes": 0,
              "shots": [ { "file": "a.png", "width": 800, "height": 600, "path": "/Inbox",
                           "storage": { "parley.nav": "collapsed" }, "storage": { "parley-convo-composer": "closed" } } ] }
            """);

        _ = Assert.Throws<InvalidDataException>(() => ShotList.Load(path));
    }

    [Fact]
    public void A_shot_naming_an_unknown_label_is_said()
    {
        var scenario = LoadScenario("""{ "project": "shop", "machine": "devbox", "agents": [], "steps": [] }""");

        var problem = Assert.Single(Placeholders.Problems("/Exchanges/Panel?id={exchange:nowhere}&agent={agent}", scenario).Take(1));

        Assert.Contains("{exchange:nowhere}", problem, StringComparison.Ordinal);
        Assert.Equal(2, Placeholders.Problems("/Exchanges/Panel?id={exchange:nowhere}&agent={agent}", scenario).Count());
    }

    private Scenario LoadScenario(string json)
    {
        var path = Path.Combine(_folder, "scenario.json");
        File.WriteAllText(path, json);

        return Scenario.Load(path);
    }

    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Parley.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The tests run from outside the repository.");
    }
}
