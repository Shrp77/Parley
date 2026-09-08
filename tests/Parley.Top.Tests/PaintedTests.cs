using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Drawing;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the surface is actually drawn in colour, and that no fact rests on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The companion to <see cref="ColourTests"/>, and the half it cannot do.</b> That suite reads
/// the screen with every attribute stripped and asserts nothing is lost, which is the property
/// that matters — but it passes just as well against a surface with no colour at all, and for a
/// year it did. The owner's word for the result was "so blah". So this one reads the driver's own
/// cell buffer, where the attribute of every cell survives, and asserts the colour is there.
/// </para>
/// <para>
/// <b>It reads the buffer rather than the string the harness usually reads.</b>
/// <c>Driving.Text</c> comes from <c>App.ToString</c>, which renders the cells and none of their
/// attributes — measured, not assumed: a screen with a bright red row and a screen with none
/// produce the identical string. <c>Driver.Contents</c> is where the two differ.
/// </para>
/// <para>
/// Every assertion below is about a colour <i>being different from another colour</i>, never about
/// which colour it is. A test naming the hue would fail on the day somebody adjusts the palette,
/// which is a change nobody should have to argue for.
/// </para>
/// </remarks>
public sealed class PaintedTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task Something_on_the_screen_is_drawn_in_a_colour()
    {
        // Non-vacuity, and it is the assertion the whole file rests on: every other test here
        // compares two colours, and a surface that drew everything in the terminal's default would
        // satisfy a badly written comparison by having no colours to tell apart.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Contains(Painted(driving), colour => colour != Color.None);
    }

    [Fact]
    public async Task The_status_bar_is_a_bar_rather_than_a_line_of_text()
    {
        // The one place a background of its own is spent, and the reason is that it is the only
        // thing always on screen: where you are, how to get out, what this region can do. Somebody
        // who is lost finds a bar without looking for it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        var driver = driving.App.Driver;

        Assert.NotNull(driver);

        var bottom = driver!.Rows - 1;
        var ground = driver.Contents![bottom, 0].Attribute?.Background;

        Assert.NotNull(ground);
        Assert.NotEqual(Color.None, ground!.Value);
    }

    [Fact]
    public void A_broker_that_answers_and_one_that_does_not_are_not_the_same_colour()
    {
        // The colour follows the word already on the line — "answering" and "not answering" are
        // both written out, and ColourTests holds that. What this adds is that having written
        // them, the surface then draws them differently.
        var region = new ProjectsRegion();

        region.Show(
            [
                new ProjectSettings(ProjectName.Create("up"), string.Empty, "http://127.0.0.1:1", null, null),
                new ProjectSettings(ProjectName.Create("down"), string.Empty, "http://127.0.0.1:2", null, null),
            ],
            new Dictionary<string, ProjectHealth>(StringComparer.Ordinal)
            {
                ["up"] = new(ProtocolVersion.Current, null),
                ["down"] = new(null, "connection refused"),
            },
            null);

        Assert.NotEqual(Drawn.Tint(region.List, 0), Drawn.Tint(region.List, 1));
    }

    [Fact]
    public void A_retired_agent_and_a_running_one_are_not_the_same_colour()
    {
        var region = new AgentsRegion();

        region.Show(
            [
                Agent("billing@here", AgentPresence.Running, AgentStatus.Retired),
                Agent("orders@here", AgentPresence.Running, AgentStatus.Active),
            ],
            NodeCatalogue.Empty,
            null);

        Assert.NotEqual(Drawn.Tint(region.List, 0), Drawn.Tint(region.List, 1));
    }

    [Fact]
    public void A_seat_is_not_drawn_as_an_agent()
    {
        // A person holding a seat is the one row in this region that is not an agent, and the one
        // row no destructive action is ever offered for. It is worth telling apart at a glance.
        var region = new AgentsRegion();

        region.Show(
            [
                Seated("ada@here"),
                Agent("orders@here", AgentPresence.Running, AgentStatus.Active),
            ],
            NodeCatalogue.Empty,
            null);

        Assert.NotEqual(Drawn.Tint(region.List, 0), Drawn.Tint(region.List, 1));
    }

    [Fact]
    public async Task The_highlighted_row_keeps_the_highlight_rather_than_a_tint()
    {
        // **The defect this exists to prevent.** A row attribute set from a tint replaces the one
        // the toolkit would have drawn, highlight included — so tinting the selected row would
        // take away the only mark saying which row a key is about to act on, in the two regions
        // where a key retires an agent and destroys a conversation.
        //
        // Driven through the whole surface rather than on a bare region, because the guard turns
        // on the list having focus, and a region that is not on a screen has none. A region built
        // alone would satisfy this test by never being focused at all.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        // **Asked of the conversations region now, and the move is the point rather than a
        // convenience.** This guard needs a row that would be tinted if it were not highlighted,
        // and the agents region has none any more: its states became group headings when the owner
        // reported them being cut off, headings are the only thing it tints, and a heading is not
        // selectable. Conversations still colour every row by whether an exchange is open, so the
        // property is checked where it still exists rather than checked against nothing.
        var region = shell.Conversations;

        // Tab onto it: a project view opens on the agents pane, and the guard turns on focus.
        shell.Move(forwards: true);

        region.Show([Discussion("open-one"), Discussion("open-two")], null);

        region.List.SelectedItem = 0;
        driving.App.LayoutAndDraw(true);

        Assert.True(region.List.HasFocus, "the conversations list should hold focus here");
        Assert.Null(Drawn.Tint(region.List, 0));
        Assert.NotNull(Drawn.Tint(region.List, 1));
    }

    [Fact]
    public void Two_senders_in_one_transcript_are_drawn_in_their_own_colours()
    {
        // What Palette says its colour is for: following one agent down a long conversation. The
        // slots are the shared derivation's, so the same agent is this colour in a browser too.
        var region = new TranscriptRegion();

        region.Show(
            "threeway",
            "alpha",
            new ConversationExport(
                [
                    Message("orders@here", "inventory@here", 1),
                    Message("inventory@here", "orders@here", 2),
                ],
                []),
            null);

        var first = AgentName.Create("orders@here");
        var second = AgentName.Create("inventory@here");

        // The fixture is only worth anything while the two fall in different slots. Palette folds
        // a name into eight, so a collision is possible in principle and would make this test pass
        // by having nothing to compare.
        Assert.NotEqual(Palette.SlotFor(first), Palette.SlotFor(second));

        var headers = Headers(region).ToList();

        Assert.Equal(2, headers.Count);
        Assert.NotEqual(headers[0], headers[1]);
    }

    [Fact]
    public void A_transcript_body_line_is_left_in_the_terminals_own_colour()
    {
        // Eight thousand characters of somebody else's words, tinted in a hue this surface chose,
        // is the opposite of the header standing out.
        var region = new TranscriptRegion();

        region.Show(
            "threeway",
            "alpha",
            new ConversationExport([Message("orders@here", "billing@here", 1)], []),
            null);

        var rows = Drawn.Rows(region.List);
        var body = rows.FindIndex(row => row.Contains("a question", StringComparison.Ordinal));

        Assert.True(body >= 0, "the payload is not on screen at all");
        Assert.Null(Drawn.Tint(region.List, body));
    }

    /// <summary>Every distinct colour anything on the screen is drawn in.</summary>
    /// <param name="driving">The session.</param>
    /// <returns>The colours.</returns>
    private static IReadOnlyList<Color> Painted(Driving.Session driving)
    {
        var driver = driving.App.Driver;

        Assert.NotNull(driver);

        var seen = new HashSet<Color>();

        for (var row = 0; row < driver!.Rows; row++)
        {
            for (var column = 0; column < driver.Cols; column++)
            {
                if (driver.Contents![row, column].Attribute is { } attribute)
                {
                    seen.Add(attribute.Foreground);
                }
            }
        }

        return [.. seen];
    }


    /// <summary>The colour of every header line in a transcript, in order.</summary>
    /// <param name="region">The region.</param>
    /// <returns>The colours.</returns>
    /// <remarks>
    /// Headers are found the way <see cref="Reading"/> guarantees they can be — at column zero,
    /// where nothing else sits — rather than by knowing which row numbers they landed on.
    /// </remarks>
    private static IEnumerable<Color?> Headers(TranscriptRegion region)
    {
        var rows = Drawn.Rows(region.List);

        for (var at = 0; at < rows.Count; at++)
        {
            if (rows[at].Contains(" -> ", StringComparison.Ordinal)
                && !rows[at].StartsWith(' '))
            {
                yield return Drawn.Tint(region.List, at);
            }
        }
    }


    private static AgentRecord Agent(string name, AgentPresence presence, AgentStatus status) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, null, null, null, true, status, null, presence);

    private static AgentRecord Seated(string name) =>
        new(
            AgentName.Create(name),
            Seat.Adapter,
            AgentTier.Pull,
            null,
            null,
            null,
            true,
            AgentStatus.Active,
            null,
            AgentPresence.Running);

    private static Message Message(string from, string to, long id) =>
        new(
            id,
            ExchangeId: null,
            MessageType.Question,
            AgentName.Create(from),
            AgentName.Create(to),
            "test",
            """{"said":"a question"}""",
            "threeway",
            DateTimeOffset.UnixEpoch,
            null);

    /// <summary>A conversation with an exchange still open, so its row earns a colour.</summary>
    /// <param name="id">Its identifier.</param>
    /// <returns>The summary.</returns>
    private static ConversationSummary Discussion(string id) =>
        new(
            id,
            [AgentName.Create("orders@here")],
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            true);

    /// <summary>The colour of one group's heading.</summary>
    /// <param name="region">The agents region.</param>
    /// <param name="heading">The heading's exact words.</param>
    /// <returns>Its colour.</returns>
    private static Color? Heading(AgentsRegion region, string heading)
    {
        var at = Drawn.Rows(region.List).IndexOf(heading);

        Assert.True(at >= 0, $"no heading reads '{heading}'");

        return Drawn.Tint(region.List, at);
    }
}
