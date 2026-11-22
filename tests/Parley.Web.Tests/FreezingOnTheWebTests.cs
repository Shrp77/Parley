using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// Freezing and unfreezing from the web console: what the agent's page offers, and what each confirmation asks the broker
/// for (SPEC.md §10.2 <c>freeze</c>, <c>unfreeze</c>; §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every act posts the real form to the real page and reads what the broker was asked.</b> The four arms differ by a
/// flag, so one swapped for another still answers and still says something happened: only the broker's record tells them
/// apart.
/// </para>
/// <para>
/// The box that keeps a freeze after a logon is read from the posted form alone, so a followed link cannot choose it.
/// </para>
/// </remarks>
[Integration]
public sealed partial class FreezingOnTheWebTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Old = "old@test";
    private const string Person = PersonSeat.Role + "@" + RoadmapConsole.Project;
    private const string Facilitator = Facilitation.Role + "@" + RoadmapConsole.Project;

    private const string FreezePath = "/parley.v1.Operators/Freeze";
    private const string UnfreezePath = "/parley.v1.Operators/Unfreeze";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirming_a_freeze_asks_the_broker_to_freeze_that_agent_kept_only_when_the_box_is_ticked(bool ticked)
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Orders));
        console.Broker.Registry.Add(Row(Billing));

        var after = await ConfirmAsync(console, "freeze", Orders, ticked ? [new("KeepFrozen", "true")] : []);

        var asked = Assert.Single(console.Broker.Frozen);

        Assert.Equal(Orders, asked.Agent);
        Assert.Equal(ticked, asked.KeepFrozen);
        Assert.Empty(console.Broker.Unfrozen);
        Assert.Equal(0, Asked(console, UnfreezePath));
        Assert.Contains($"{RoadmapConsole.Project}: changed", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_an_unfreeze_asks_the_broker_to_unfreeze_that_agent_and_freezes_nobody()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Orders, frozen: true));
        console.Broker.Registry.Add(Row(Billing, frozen: true));

        // The box is not on this form. One posted anyway must not turn the act into a freeze.
        var after = await ConfirmAsync(console, "unfreeze", Orders, [new("KeepFrozen", "true")]);

        Assert.Equal(Orders, Assert.Single(console.Broker.Unfrozen).Agent);
        Assert.Empty(console.Broker.Frozen);
        Assert.Equal(0, Asked(console, FreezePath));
        Assert.Contains($"{RoadmapConsole.Project}: changed", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirming_a_freeze_of_the_project_freezes_each_agent_that_can_be_and_is_not_and_unfreezes_nobody(bool ticked)
    {
        using var console = Everyone();

        var after = await ConfirmAsync(
            console, "freezeall", RoadmapConsole.Project, ticked ? [new("KeepFrozen", "true")] : []);

        var asked = Assert.Single(console.Broker.Frozen);

        Assert.Equal(Orders, asked.Agent);
        Assert.Equal(ticked, asked.KeepFrozen);
        Assert.Empty(console.Broker.Unfrozen);
        Assert.Equal(0, Asked(console, UnfreezePath));
        Assert.Contains("1 agent frozen", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_an_unfreeze_of_the_project_unfreezes_each_frozen_agent_and_freezes_nobody()
    {
        using var console = Everyone();

        var after = await ConfirmAsync(console, "unfreezeall", RoadmapConsole.Project, [new("KeepFrozen", "true")]);

        Assert.Equal(Billing, Assert.Single(console.Broker.Unfrozen).Agent);
        Assert.Empty(console.Broker.Frozen);
        Assert.Equal(0, Asked(console, FreezePath));
        Assert.Contains("1 agent unfrozen", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("freeze", Orders)]
    [InlineData("freezeall", RoadmapConsole.Project)]
    public async Task Keeping_a_freeze_is_read_from_the_posted_form_and_never_from_the_link(string action, string subject)
    {
        using var console = Everyone();

        // The link says to keep it and the form says nothing: a followed link must not choose for the person.
        _ = await ConfirmAsync(console, action, subject, [], query: "&KeepFrozen=true");

        Assert.False(Assert.Single(console.Broker.Frozen).KeepFrozen);
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("unfreeze")]
    [InlineData("freezeall")]
    [InlineData("unfreezeall")]
    public async Task Describing_an_act_changes_nothing(string action)
    {
        using var console = Everyone();
        using var client = console.CreateClient();

        var subject = action.EndsWith("all", StringComparison.Ordinal) ? RoadmapConsole.Project : Orders;

        _ = await client.GetStringAsync(Address(action, subject, string.Empty), Ct);

        Assert.Equal(0, Asked(console, FreezePath));
        Assert.Equal(0, Asked(console, UnfreezePath));
    }

    [Fact]
    public async Task A_frozen_agents_page_offers_unfreeze_and_not_freeze()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Orders, frozen: true));

        var page = await AgentPageAsync(console, Orders);

        Assert.Contains("action=unfreeze&subject=" + Orders, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=freeze&subject=", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_is_not_frozen_is_offered_freeze_and_not_unfreeze()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Orders));

        var page = await AgentPageAsync(console, Orders);

        Assert.Contains("action=freeze&subject=" + Orders, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=unfreeze&subject=", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retired_agents_page_offers_neither_frozen_or_not(bool frozen)
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Old, Wire.AgentStatus.Retired, frozen));

        var page = await AgentPageAsync(console, Old);

        // The page is the retired agent's, so what it does offer is there and the two freezes are not.
        Assert.Contains("action=reinstate&subject=" + Old, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=freeze&subject=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=unfreeze&subject=", page, StringComparison.Ordinal);
    }

    /// <summary>A project with one agent to freeze, one already frozen, and the three that are never frozen from here.</summary>
    private static RoadmapConsole Everyone()
    {
        var console = new RoadmapConsole();

        console.Broker.Registry.Add(Row(Orders));
        console.Broker.Registry.Add(Row(Billing, frozen: true));
        console.Broker.Registry.Add(Row(Old, Wire.AgentStatus.Retired));
        console.Broker.Registry.Add(Row(Person, adapter: PersonSeat.Adapter));
        console.Broker.Registry.Add(Row(Facilitator, adapter: Facilitation.Adapter));

        return console;
    }

    private static Wire.RegistryRow Row(
        string agent,
        Wire.AgentStatus status = Wire.AgentStatus.Active,
        bool frozen = false,
        string adapter = "claude-code") => new()
        {
            Name = agent,
            Adapter = adapter,
            Tier = Wire.AgentTier.Full,
            Status = status,
            FrozenUtc = frozen
            ? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty,
        };

    private static int Asked(RoadmapConsole console, string path) => console.Broker.Paths.Count(p => p == path);

    private static Uri Address(string action, string subject, string query) => new(
        $"/Projects/Act?project={RoadmapConsole.Project}&action={action}&subject={Uri.EscapeDataString(subject)}{query}",
        UriKind.Relative);

    /// <summary>Reads the page that describes an act, then posts its form back to it with the fields given.</summary>
    private static async Task<string> ConfirmAsync(
        RoadmapConsole console,
        string action,
        string subject,
        IEnumerable<KeyValuePair<string, string>> fields,
        string query = "")
    {
        using var client = console.CreateClient();

        var described = await client.GetStringAsync(Address(action, subject, string.Empty), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(described).Groups[1].Value),
            .. fields,
        ]);

        using var response = await client.PostAsync(Address(action, subject, query), form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> AgentPageAsync(RoadmapConsole console, string agent)
    {
        using var client = console.CreateClient();

        return WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Agent?project={RoadmapConsole.Project}&agent={Uri.EscapeDataString(agent)}", UriKind.Relative), Ct));
    }
}
