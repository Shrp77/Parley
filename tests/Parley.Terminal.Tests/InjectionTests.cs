using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That nothing an agent wrote can repaint the screen an operator is deciding on.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, probing every route by which text this program did not write reaches
/// the terminal. There are four: an agent's own <c>owns</c> line and its name, from the registry; a
/// message payload, from a transcript; a project's description, from the manifest; and a log line,
/// from whatever the broker wrote.
/// </para>
/// <para>
/// <b>The screen this matters most on is the one a purge is confirmed from.</b> That dialog quotes
/// the agent back at the person about to remove it — its name, what it says it owns, what it owes —
/// so an agent able to put an escape sequence in its own registration could repaint the description
/// somebody is reading to decide, and there is no other check between that description and the act.
/// </para>
/// </remarks>
public sealed class InjectionTests
{
    /// <summary>An escape sequence that clears the screen, which is the loudest of the family.</summary>
    private const string Hostile = "\u001b[2J";

    private static AgentRecord Agent(string name, string? owns) =>
        new(AgentName.Create(name), "mcp", AgentTier.Pull, "/srv" + Hostile, null, null, true,
            AgentStatus.Active, owns, AgentPresence.Running);

    [Fact]
    public void An_agents_own_ownership_line_cannot_repaint_the_agents_region()
    {
        // The line is the agent's own claim about itself (§10.1) and reaches the registry from its
        // launch configuration, so it is exactly as trustworthy as whoever wrote that file.
        var row = Assert.Single(AgentsRegion.Join(
            [Agent("orders@test", "carts" + Hostile + " and totals")],
            NodeCatalogue.Empty));

        Assert.DoesNotContain(Hostile, row.Owns, StringComparison.Ordinal);
        Assert.Contains("carts", row.Owns, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agents_workspace_cannot_repaint_it_either()
    {
        // Reported by the agent's own adapter about where it ran, so it is a string from the
        // network rather than a path this machine chose.
        var row = Assert.Single(AgentsRegion.Join(
            [Agent("orders@test", null)], NodeCatalogue.Empty));

        Assert.DoesNotContain(Hostile, row.Workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void A_catalogue_name_somebody_hand_edited_cannot_repaint_it()
    {
        // The catalogue is a local file, which makes it trusted in the sense that matters for
        // §13 — and not in this one. Somebody who edits it by hand can put anything in a key.
        var path = Path.Combine(Path.GetTempPath(), "parley-injection-" + Path.GetRandomFileName() + ".json");

        File.WriteAllText(
            path,
            "{\n  \"agents\": {\n    \"orders\\u001b[2J\": { \"command\": \"claude\" }\n  }\n}\n");

        try
        {
            var row = Assert.Single(AgentsRegion.Join([], NodeCatalogue.ReadFrom(path)));

            Assert.DoesNotContain(Hostile, row.Name, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_message_cannot_repaint_the_transcript_it_is_read_in()
    {
        // A payload is agent-written and capped at eight thousand characters, which is a great many
        // escape sequences. The transcript is the longest thing on this surface and the one a
        // person reads rather than glances at.
        var region = new TranscriptRegion();

        var message = new Message(
            1,
            1,
            MessageType.Request,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "test",
            "{\"said\":\"before" + Hostile + "after\"}",
            "conv",
            DateTimeOffset.UnixEpoch,
            null);

        region.Show("conv", "alpha", new ConversationExport([message], []), null);

        Assert.DoesNotContain(Hostile, region.Written, StringComparison.Ordinal);
        Assert.Contains("before", region.Written, StringComparison.Ordinal);
        Assert.Contains("after", region.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_a_purge_is_confirmed_from_cannot_be_repainted()
    {
        // The screen with the least between it and an irreversible act. Everything quoted in it
        // comes from the agent being removed.
        var row = Assert.Single(AgentsRegion.Join(
            [Agent("orders@test", "carts" + Hostile)], NodeCatalogue.Empty));

        var said = Withdrawing.Describe(null, row, []);

        Assert.DoesNotContain(Hostile, said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_cannot_be_reached_cannot_repaint_the_region_it_reports_in()
    {
        // The problem text is a transport failure's message, which carries whatever the endpoint
        // string was -- and that comes from the manifest.
        var region = new AgentsRegion();

        region.Show([], NodeCatalogue.Empty, "could not reach " + Hostile);

        var lines = new List<string>();

        foreach (var row in region.List.Source!.ToList())
        {
            lines.Add(row?.ToString() ?? string.Empty);
        }

        Assert.All(lines, line => Assert.DoesNotContain(Hostile, line, StringComparison.Ordinal));
    }

    [Fact]
    public void A_peers_request_cannot_repaint_the_form_it_is_answered_from()
    {
        // Found by the review gate, lens 2 run 2.9. A Request's intent, items, done_when and item ids are the
        // requester's words, and they are quoted into the Result and clarification forms' titles, the chooser of
        // owed messages and the Result form's labels — none of which went through Safe.
        var owed = new Parley.Client.Seating.Delivered(new Message(
            1,
            1,
            MessageType.Request,
            AgentName.Create("orders@test"),
            AgentName.Create("arvind@test"),
            "test",
            "{\"intent\":\"before" + HostileInJson + "after\","
            + "\"items\":[{\"id\":\"r1" + HostileInJson + "\",\"kind\":\"work\",\"text\":\"item" + HostileInJson + "\"}],"
            + "\"done_when\":\"done" + HostileInJson + " when the field is moved\"}",
            "conv",
            DateTimeOffset.UnixEpoch,
            null));

        var asked = Participation.Asked(owed);
        var id = Assert.Single(owed.Items());

        Assert.Contains(Hostile, id, StringComparison.Ordinal);

        Assert.DoesNotContain(Hostile, asked, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, Participation.Label(owed), StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, Participation.OutcomeLabel(id), StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, Participation.SaidLabel(id), StringComparison.Ordinal);

        Assert.Contains("before", asked, StringComparison.Ordinal);
        Assert.Contains("after", asked, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, asked, StringComparison.Ordinal);
    }

    /// <summary>The same sequence as JSON escapes it inside a string, which is how a peer's payload carries it.</summary>
    private const string HostileInJson = "\\u001b[2J";

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the fixture really does carry the sequence, so a pass above means it was
        // taken out rather than never present.
        Assert.Contains(Hostile, Agent("orders@test", "carts" + Hostile).Owns!, StringComparison.Ordinal);
        Assert.Contains(Hostile, Agent("orders@test", null).Workspace!, StringComparison.Ordinal);
    }
}
