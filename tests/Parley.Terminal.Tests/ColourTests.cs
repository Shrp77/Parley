using Parley.Client.Seating;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That nothing this surface draws means anything by colour alone, and that nothing it draws can
/// be made to draw something else.
/// </summary>
/// <remarks>
/// <para>
/// SC-006. This matters more in a terminal than in a browser, because a terminal may genuinely
/// have no colour: a pipe, a CI log, a session over a bad connection, or a reader who cannot
/// distinguish the two the surface happened to pick.
/// </para>
/// <para>
/// <b>The injection half changed shape with the toolkit and got worse rather than better.</b> The
/// Spectre version defended against markup, because that library reads square brackets as style
/// tags. This toolkit reads nothing — it draws strings as characters — so what is left is the
/// older and more serious hazard: an escape sequence in an agent's payload or a broker's log moves
/// the cursor and repaints rows the surface believes it owns, which can put text on screen that
/// appears to have come from the surface itself.
/// </para>
/// </remarks>
public sealed class ColourTests
{
    [Theory]
    [InlineData(AgentPresence.Running, AgentStatus.Active, "running")]
    [InlineData(AgentPresence.Startable, AgentStatus.Active, "startable")]
    [InlineData(AgentPresence.Stranded, AgentStatus.Active, "stranded")]
    [InlineData(AgentPresence.Running, AgentStatus.Retired, "retired")]
    public void An_agents_state_reads_the_same_without_colour(
        AgentPresence presence, AgentStatus status, string expected)
    {
        // The four states that matter, and the one distinction the specification calls out by
        // name: startable and stranded both look like "not here", and only one of them means a
        // peer cannot be asked. A surface that separated them by colour alone would collapse them
        // for every reader who has none.
        var row = Assert.Single(AgentsRegion.Join([Agent("orders@here", presence, status)], NodeCatalogue.Empty));

        Assert.StartsWith(expected, row.State, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_that_stated_nothing_says_so_in_words()
    {
        // An absent claim is a fact, not a gap. Rendering it as an empty cell would read as a
        // column that failed rather than as something the agent never said.
        var row = Assert.Single(AgentsRegion.Join(
            [Agent("orders@here", AgentPresence.Running, AgentStatus.Active)],
            NodeCatalogue.Empty));

        Assert.Equal("not stated", row.Owns);
    }

    [Fact]
    public void Who_a_message_is_going_to_is_carried_as_text()
    {
        // **This test was about the visibility annotation and is about the checklist now.** That sentence
        // left the screen when the owner asked for it -- the header names one sender and one recipient, and
        // this column lists every participant -- and the rule it stood for found a better instance on the way
        // out: who a message is about to be sent to is exactly the kind of fact that must not be a highlight.
        var room = new RoomRegion();
        var billing = AgentName.Create("billing@here");
        var orders = AgentName.Create("orders@here");

        room.Show(
            [new PairState(billing, null, Turn.Nobody), new PairState(orders, 7, Turn.Mine)],
            [],
            seated: true,
            AgentName.Create("arvind@here"),
            [billing]);

        Assert.Contains(
            room.Written,
            row => row.StartsWith(RoomRegion.Addressed, StringComparison.Ordinal)
                && row.Contains(billing.Role, StringComparison.Ordinal));

        Assert.Contains(
            room.Written,
            row => row.StartsWith(RoomRegion.NotAddressed, StringComparison.Ordinal)
                && row.Contains(orders.Role, StringComparison.Ordinal));
    }

    [Fact]
    public void A_broker_that_disagrees_about_the_version_names_both_numbers()
    {
        // "Incompatible" without the two numbers cannot be acted on, and a colour saying
        // "something is wrong here" is the version of that with even less information.
        var said = ProjectsRegion.Describe(new ProjectHealth("99.9", null));

        Assert.Contains("99.9", said, StringComparison.Ordinal);
        Assert.Contains(ProtocolVersion.Current, said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_is_not_answering_says_so_rather_than_going_red()
    {
        Assert.Equal(
            ProjectsRegion.NotAnswering,
            ProjectsRegion.Describe(new ProjectHealth(null, "connection refused")));
    }

    [Fact]
    public void A_message_cannot_move_the_cursor_of_the_terminal_it_is_read_in()
    {
        // The injection, in the shape this toolkit has. A payload is agent-written and may contain
        // anything; an escape sequence in one would otherwise let a sender repaint rows around
        // their own words, or clear the screen the surface believes it is drawing.
        var hostile = Message(
            "orders@here",
            "billing@here",
            1,
            payload: "{\"said\":\"before\u001b[2Jafter\\rand this\"}");

        var drawn = Safe.Text(hostile.PayloadJson);

        Assert.DoesNotContain("\u001b", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", drawn, StringComparison.Ordinal);

        // Still readable, and still the same length: a filter that dropped the characters would
        // change an identifier's length silently, and on this surface a name typed back is what
        // stands between an agent and being purged.
        Assert.Contains("before", drawn, StringComparison.Ordinal);
        Assert.Contains("after", drawn, StringComparison.Ordinal);
        Assert.Equal(hostile.PayloadJson.Length, drawn.Length);
    }

    [Fact]
    public void A_project_named_with_control_characters_cannot_repaint_the_list()
    {
        // The same hazard reaching the surface by its other route. A manifest is a file somebody
        // may not have written themselves, and the project list is drawn before anything is asked
        // of a broker -- so this is the first text on screen and the earliest chance to be lied to.
        var region = new ProjectsRegion();

        region.Show(
            [
                new ProjectSettings(
                    ProjectName.Create("orders"),
                    string.Empty,
                    "http://127.0.0.1:1",
                    "quiet\u001b[31m and red"),
            ],
            new Dictionary<string, ProjectHealth>(StringComparer.Ordinal),
            null);

        var lines = new List<string>();

        for (var at = 0; at < region.List.Source!.Count; at++)
        {
            lines.Add(region.List.Source.ToList()[at]?.ToString() ?? string.Empty);
        }

        Assert.All(lines, line => Assert.DoesNotContain("\u001b", line, StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_line_cannot_repaint_the_view_that_follows_it()
    {
        // The third route, and the one that is not the deployment's own text at all: a broker's
        // log holds whatever an agent's own output put in it.
        Assert.DoesNotContain(
            "\u001b",
            Safe.Text("started\u001b[2Jand cleared your screen"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, and it replaces 004's version of the same idea. That one proved the
        // renderer really did interpret markup, so that escaping it was defending against
        // something rather than nothing. There is no markup now, so what has to be shown is that
        // the characters being neutralised are characters that were actually there: a Safe.Text
        // which returned its input unchanged would pass every assertion above.
        const string Hostile = "before\u001b[2Jafter";

        Assert.NotEqual(Hostile, Safe.Text(Hostile));
        Assert.Contains("\u001b", Hostile, StringComparison.Ordinal);
        Assert.Equal(Hostile.Length, Safe.Text(Hostile).Length);
    }

    [Fact]
    public void A_tab_survives_because_it_is_laid_out_rather_than_obeyed()
    {
        // Log lines are full of them, and replacing one would make every log unreadable to defend
        // against something a tab cannot do.
        Assert.Equal("a\tb", Safe.Text("a\tb"));
    }

    private static AgentRecord Agent(string name, AgentPresence presence, AgentStatus status) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, null, null, null, true, status, null, presence);

    private static Message Message(string from, string to, long id, string? payload = null) =>
        new(
            id,
            ExchangeId: 1,
            MessageType.Request,
            AgentName.Create(from),
            AgentName.Create(to),
            "test",
            payload ?? """{"said":"a question"}""",
            "threeway",
            DateTimeOffset.UnixEpoch,
            null);
}
