using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the Inbox notices what changed without a reload by hand, and that noticing has no effects.
/// </summary>
/// <remarks>
/// <b>The owner's report, 2026-11-06:</b> the Inbox showed nothing new until it was reloaded. The page now asks for a
/// fingerprint of what waits on or for the person every few seconds and reloads when it moves. These hold the fingerprint:
/// that the page draws the one it asks for, that it moves when something changes, and that asking it collects nothing.
/// </remarks>
[Integration]
public sealed partial class TheInboxFollowsWhatChangesTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private static readonly Uri InboxUri = new("/Inbox", UriKind.Relative);
    private static readonly Uri DigestUri = new("/Inbox?handler=Digest", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""data-digest="([^"]*)""")]
    private static partial Regex Drawn();

    private static RoadmapConsole Seated()
    {
        var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Person,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        return console;
    }

    private static async Task<string> DigestAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync(DigestUri, Ct)).RootElement.GetProperty("digest").GetString()!;

    [Fact]
    public async Task The_page_draws_the_fingerprint_it_asks_for_and_it_moves_when_something_changes()
    {
        using var console = Seated();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(InboxUri, Ct);
        var drawn = System.Net.WebUtility.HtmlDecode(Drawn().Match(page).Groups[1].Value);

        Assert.NotEmpty(drawn);
        Assert.Equal(drawn, await DigestAsync(client));

        // The question was answered somewhere: its edge is gone.
        console.Broker.Edges.Clear();
        Assert.NotEqual(drawn, await DigestAsync(client));

        // One of the person's own requests, now waited on: that moves it too.
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 2 });
        Assert.NotEqual(drawn, await DigestAsync(client));
    }

    [Fact]
    public async Task Asking_for_the_fingerprint_collects_nothing_and_seats_nobody()
    {
        using var console = Seated();
        using var client = console.CreateClient();

        await DigestAsync(client);

        Assert.DoesNotContain(console.Broker.Paths, path => path.StartsWith("/parley.v1.Agents/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_reload_keeps_the_filters_and_is_marked_as_the_pages_own()
    {
        using var console = Seated();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?q=invoice&scope=alpha", UriKind.Relative), Ct);
        var url = System.Net.WebUtility.HtmlDecode(Regex.Match(page, """id="inbox-changed"[^>]*data-url="([^"]*)""").Groups[1].Value);

        Assert.Equal("/Inbox?q=invoice&scope=alpha&reloaded=1", url);
    }
}
