using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Typing <c>@</c> offers the project's agents in every message box, through the composer (ComposerBox).</summary>
/// <remarks>
/// <b>The owner, 2026-11-13:</b> "In the Inbox view, I cannot use the '@' symbol to pop up the agents in the related
/// project." Only the conversation page carried the list, in a script of its own.
/// </remarks>
[Integration]
public sealed class MentionsInEveryMessageBoxTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private static string Layout => ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

    private static string Mentions => ScriptSource.Code(Layout, "list.id = \"mention-list\";");

    [Fact]
    public void Every_message_box_offers_mentions()
    {
        // Drawn by what the box is: a message box keeps a draft, and every one offers the project's agents as well.
        var boxes = Directory.GetFiles(ScriptSource.RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"new Parley\.Web\.ComposerBox\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(2))
                .Select(match => (Page: Path.GetFileName(path), Box: match.Value)))
            .Where(found => found.Box.Contains("Draft: $\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(boxes.Count >= 6, $"only {boxes.Count} message boxes were found; the pattern no longer reads them");
        Assert.Empty(boxes.Where(found => !found.Box.Contains("Mentions: ", StringComparison.Ordinal)).Select(found => found.Page));
    }

    [Fact]
    public void Only_the_frame_runs_the_list()
    {
        var running = Directory.GetFiles(ScriptSource.RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("mention-list", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["_Layout.cshtml"], running);
    }

    [Fact]
    public void The_list_takes_its_keys_before_the_enter_that_sends()
    {
        // Without the capture phase, Enter chose nothing and sent the half-typed message instead.
        Assert.EndsWith("}, true); })();", Mentions.TrimEnd(), StringComparison.Ordinal);
        ScriptSource.Once(
            Mentions,
            "} else if (e.key === \"Enter\" || e.key === \"Tab\") { e.preventDefault(); e.stopPropagation(); choose(shown[at]);",
            "Enter or Tab no longer chooses, and the frame sends instead");
    }

    [Fact]
    public void A_name_is_only_ever_ticked_through_the_forms_own_box()
    {
        // The words never address anybody (SPEC.md §13): only a Targets box the form already carries is ticked.
        ScriptSource.Once(
            Mentions,
            "return el instanceof HTMLInputElement && el.name === \"Targets\" && el.value === name;",
            "a mention no longer looks for the form's own box");
        Assert.DoesNotContain("name=\"To\"", Mentions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inboxs_boxes_offer_the_projects_agents_and_not_the_person()
    {
        using var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow { Name = Person, Adapter = "parley-console", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        console.Broker.Registry.Add(new Wire.RegistryRow { Name = Orders, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        console.Broker.Registry.Add(new Wire.RegistryRow { Name = Billing, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "shared", HasOpenExchange = true });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 1,
            ExchangeId = 1,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = """{"ask":"Which schema?"}""",
            ConversationId = "shared",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 1,
            State = Wire.ExchangeState.Open,
            OpenedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        using var client = console.CreateClient();
        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), TestContext.Current.CancellationToken);

        foreach (var id in new[] { "t-1-request", "c-1" })
        {
            var box = Regex.Match(page, $"<textarea data-composer id=\"{id}\"[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(2)).Value;

            Assert.Contains($"data-mentions=\"{Billing} {Orders}\"", box, StringComparison.Ordinal);
        }
    }
}
