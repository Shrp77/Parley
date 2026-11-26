using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The graph panel shows what is being worked on now: each working agent, what it holds, what waits for it, and a
/// filter by project and agent.</summary>
/// <remarks>
/// <para>
/// Feature 042, the person's feedback on the graph: an agent not at work is left out, and the picture can be narrowed to the
/// projects and agents chosen, each choice shown as a chip that takes it off.
/// </para>
/// <para>
/// <b>Reading it changes nothing.</b> What an agent holds is read from the conversation's record, which takes no delivery; and
/// the record is read only for an agent at work.
/// </para>
/// </remarks>
[Integration]
public sealed class TheGraphShowsWorkUnderWayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static void Script(ScriptedExport broker)
    {
        var now = DateTimeOffset.UtcNow;

        broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "billing@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true,
            TurnsReported = true, TurnRunningSinceUtc = Utc(now.AddMinutes(-4)), BackgroundTasks = 2,
        });
        broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "orders@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true, TurnsReported = true, LastTurnEndedUtc = Utc(now.AddHours(-1)),
        });

        // Exchange 5 it collected; exchange 6 waits for it, not collected.
        broker.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 5, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"ask":"How should gift cards be taxed?"}""", ConversationId = "gift-cards",
            CreatedUtc = Utc(now.AddMinutes(-6)), DeliveredUtc = Utc(now.AddMinutes(-5)),
        });
        broker.Messages.Add(new Wire.Message
        {
            Id = 2, ExchangeId = 6, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"ask":"Refund the returned order"}""", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-2)),
        });
        broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 5 });
        broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 6 });
    }

    [Fact]
    public async Task A_working_agent_is_drawn_with_what_it_holds_what_waits_for_it_and_its_background_tasks()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker);
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Contains(">billing@pc</text>", fragment, StringComparison.Ordinal);
        Assert.Contains("NOW · alpha", fragment, StringComparison.Ordinal);
        Assert.Contains("How should gift cards be taxed?", fragment, StringComparison.Ordinal);
        Assert.Contains("QUEUED 1 · alpha", fragment, StringComparison.Ordinal);
        Assert.Contains("Background task 2", fragment, StringComparison.Ordinal);

        // The idle agent is left out of the picture and the list, and counted.
        Assert.DoesNotContain(">orders@pc</text>", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>orders@pc</h3>", fragment, StringComparison.Ordinal);
        Assert.Contains("1 agent not working is hidden", fragment, StringComparison.Ordinal);

        // Every fact is in the list too, with each agent's Freeze. Freezing a whole project moved to its Agents page, and the
        // list is drawn only while the focus is in it (the person's feedback on feature 042).
        Assert.Contains("Queued 1, not collected yet", fragment, StringComparison.Ordinal);
        Assert.Contains("action=freeze&amp;subject=billing%40pc", fragment, StringComparison.Ordinal);
        Assert.Contains("<section class=\"pg-list\"", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("action=freezeall", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("Every project", fragment, StringComparison.Ordinal);

        // The agent can be folded from its box, and the zoom buttons are over the picture.
        Assert.Contains("data-pg-fold=\"billing@pc\" aria-expanded=\"true\"", fragment, StringComparison.Ordinal);
        Assert.Contains("<div class=\"pg-tools\" role=\"group\" aria-label=\"Zoom\">", fragment, StringComparison.Ordinal);

        // The record was read whole, which takes no delivery.
        Assert.All(console.Broker.ExportsAsked, asked => Assert.False(asked.LatestOnly));
    }

    [Fact]
    public async Task A_folded_agent_is_drawn_alone_and_the_list_still_says_all_it_has()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker);
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph?fold=billing%40pc&fold=bad%0Aname", UriKind.Relative), Ct);

        Assert.Contains(">billing@pc</text>", fragment, StringComparison.Ordinal);
        Assert.Contains("data-pg-fold=\"billing@pc\" aria-expanded=\"false\"", fragment, StringComparison.Ordinal);
        Assert.Contains("4 items folded", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("NOW · alpha", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("Background task 1", fragment, StringComparison.Ordinal);
        Assert.Contains("Queued 1, not collected yet", fragment, StringComparison.Ordinal);
        Assert.Contains("2 background tasks running:", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freezing_every_agent_in_a_project_is_on_its_agents_page()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/?tab=agents&project=alpha", UriKind.Relative), Ct);

        Assert.Contains("action=freezeall&amp;subject=alpha", page, StringComparison.Ordinal);
        Assert.Contains("action=unfreezeall&amp;subject=alpha", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_an_agent_narrows_the_picture_and_shows_the_choice_as_a_chip()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker);
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph?agent=orders%40pc", UriKind.Relative), Ct);

        Assert.Contains("Nothing is being worked on right now in what is chosen", fragment, StringComparison.Ordinal);
        Assert.Contains("data-pg-drop=\"agent\" value=\"orders@pc\"", fragment, StringComparison.Ordinal);
        Assert.Matches("<input type=\"checkbox\" name=\"agent\" value=\"orders@pc\" checked", fragment);

        // Nothing at work was chosen, so no conversation was read.
        Assert.Equal(0, console.Broker.Exports);
    }

    [Fact]
    public async Task A_project_the_console_does_not_serve_is_not_a_choice()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker);
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph?project=elsewhere", UriKind.Relative), Ct);

        Assert.DoesNotContain("elsewhere", fragment, StringComparison.Ordinal);
        Assert.Contains("NOW · alpha", fragment, StringComparison.Ordinal);
    }
}
