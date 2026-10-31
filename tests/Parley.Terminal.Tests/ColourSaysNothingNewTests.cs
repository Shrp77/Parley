using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That no two rows are told apart by colour alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The precise form of SC-006, and the one the older suite could not check.</b>
/// <c>ColourTests</c> asserts that each state reads correctly with attributes stripped, which is
/// necessary and was true of a surface with no colour at all. What it cannot see is the mapping
/// that arrived with the colour: whether two rows drawn in <i>different</i> colours also differ in
/// their words.
/// </para>
/// <para>
/// That is the whole rule <c>Colours</c> is written under — every colour is derived from a word
/// already on the line — turned into a check. A tint that split rows the text does not split would
/// be a fact existing only as a colour, which is what FR-030 forbids and what a reader with no
/// colour, a pipe, or a CI log would lose.
/// </para>
/// </remarks>
[Integration]
public sealed class ColourSaysNothingNewTests
{
    [Fact]
    public void Agents_drawn_in_different_colours_read_differently()
    {
        var region = new AgentsRegion();

        region.Show(
            [
                Agent("aa@here", AgentPresence.Running, AgentStatus.Active),
                Agent("bb@here", AgentPresence.Running, AgentStatus.Retired),
                Agent("cc@here", AgentPresence.Startable, AgentStatus.Active),
                Agent("dd@here", AgentPresence.Stranded, AgentStatus.Active),
                Seated("ee@here"),
            ],
            NodeCatalogue.Empty,
            null);

        NoColourOnlyDistinction(region.List);
    }

    [Fact]
    public void Projects_drawn_in_different_colours_read_differently()
    {
        var region = new ProjectsRegion();

        region.Show(
            [
                Project("up"),
                Project("down"),
                Project("asking"),
                Project("older"),
            ],
            new Dictionary<string, ProjectHealth>(StringComparer.Ordinal)
            {
                ["up"] = new(ProtocolVersion.Current, null),
                ["down"] = new(null, "connection refused"),
                ["older"] = new("0.0.1", null),
            },
            null);

        NoColourOnlyDistinction(region.List);
    }

    [Fact]
    public void Conversations_drawn_in_different_colours_read_differently()
    {
        var region = new ConversationsRegion();

        region.Show(
            [
                Conversation("open-one", open: true),
                Conversation("settled-one", open: false),
            ],
            null);

        NoColourOnlyDistinction(region.List);
    }


    [Fact]
    public void States_a_person_has_to_tell_apart_are_not_the_same_colour()
    {
        // **The converse of everything above, and the half that makes the colour worth having.**
        // The other tests forbid a colour saying more than the words; this one forbids it saying
        // less. Five standings share one list, and a person scanning it for "which of these can I
        // ask right now" is reading the colour first.
        var region = new AgentsRegion();

        region.Show(
            [
                Agent("aa@here", AgentPresence.Running, AgentStatus.Active),
                Agent("bb@here", AgentPresence.Running, AgentStatus.Retired),
                Agent("cc@here", AgentPresence.Startable, AgentStatus.Active),
                Agent("dd@here", AgentPresence.Stranded, AgentStatus.Active),
                Seated("ee@here"),
            ],
            NodeCatalogue.Empty,
            null);

        // **Asked of the headings, because they are what names a state now.** The states used to be
        // one per row, in brackets after the name; they are group headings since the owner reported
        // that a half-width pane cut them off. An agent's own row carries no state and takes no
        // colour, so comparing every row against every other would be asserting that two names are
        // different colours — which is not a fact about anything.
        var rows = Drawn.Rows(region.List);
        var tints = Drawn.Tints(region.List);

        var headings = Enumerable.Range(0, rows.Count)
            .Where(at => !rows[at].StartsWith("  ", StringComparison.Ordinal))
            .ToList();

        // Non-vacuity: a version that stopped drawing headings, or indented them, would compare
        // nothing at all and pass.
        Assert.True(headings.Count >= 4, $"only {headings.Count} headings were drawn");

        foreach (var one in headings)
        {
            foreach (var two in headings.Where(other => other > one))
            {
                if (Phrase(rows[one]) == Phrase(rows[two]))
                {
                    continue;
                }

                Assert.True(
                    tints[one] != tints[two],
                    $"'{Phrase(rows[one])}' and '{Phrase(rows[two])}' are different states drawn "
                    + "in the same colour");
            }
        }
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the fixtures must actually produce more than one colour, or "different
        // colours read differently" is true because there are no different colours.
        var region = new AgentsRegion();

        region.Show(
            [
                Agent("aa@here", AgentPresence.Running, AgentStatus.Active),
                Agent("bb@here", AgentPresence.Running, AgentStatus.Retired),
            ],
            NodeCatalogue.Empty,
            null);

        Assert.True(Drawn.Tints(region.List).Distinct().Count() > 1, "the fixture drew only one colour");
    }

    /// <summary>
    /// Fails when two rows differ in colour and not in the words that explain it.
    /// </summary>
    /// <param name="list">The list to read.</param>
    /// <remarks>
    /// Compared on the bracketed phrase each region writes rather than on the whole row, because
    /// every row also carries a name, and comparing whole rows would pass on the name alone —
    /// which says nothing about the state the colour is claiming.
    /// </remarks>
    private static void NoColourOnlyDistinction(ListView list)
    {
        var rows = Drawn.Rows(list);
        var tints = Drawn.Tints(list);

        for (var one = 0; one < rows.Count; one++)
        {
            for (var two = one + 1; two < rows.Count; two++)
            {
                if (tints[one] == tints[two])
                {
                    continue;
                }

                Assert.True(
                    Phrase(rows[one]) != Phrase(rows[two]),
                    $"'{rows[one]}' and '{rows[two]}' are drawn in different colours and say the "
                    + "same thing about their state");
            }
        }
    }

    /// <summary>The bracketed phrase a region writes to say where something stands.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The phrase, or the whole row when it has none.</returns>
    private static string Phrase(string row)
    {
        var opened = row.IndexOf('[', StringComparison.Ordinal);
        var closed = row.IndexOf(']', StringComparison.Ordinal);

        return opened >= 0 && closed > opened ? row[opened..(closed + 1)] : row;
    }


    private static AgentRecord Agent(string name, AgentPresence presence, AgentStatus status) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, null, null, null, true, status, null, presence);

    private static AgentRecord Seated(string name) =>
        new(
            AgentName.Create(name),
            Parley.Client.Seating.Seat.Adapter,
            AgentTier.Pull,
            null,
            null,
            null,
            true,
            AgentStatus.Active,
            null,
            AgentPresence.Running);

    private static ProjectSettings Project(string name) =>
        new(ProjectName.Create(name), string.Empty, "http://127.0.0.1:1", null);

    private static ConversationSummary Conversation(string id, bool open) =>
        new(id, [AgentName.Create("orders@here")], 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, open);
}
