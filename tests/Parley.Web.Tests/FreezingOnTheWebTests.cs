using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// Freezing and unfreezing from the web console: what the agent's page offers, and what each confirmation asks the broker
/// for (SPEC.md §10.2 <c>freeze</c>, <c>unfreeze</c>; §14).
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
        Assert.Contains("1 of 1 agent frozen", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_an_unfreeze_of_the_project_unfreezes_each_frozen_agent_and_freezes_nobody()
    {
        using var console = Everyone();

        var after = await ConfirmAsync(console, "unfreezeall", RoadmapConsole.Project, [new("KeepFrozen", "true")]);

        Assert.Equal(Billing, Assert.Single(console.Broker.Unfrozen).Agent);
        Assert.Empty(console.Broker.Frozen);
        Assert.Equal(0, Asked(console, FreezePath));
        Assert.Contains("1 of 1 agent unfrozen", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("freezeall", "nothing was frozen")]
    [InlineData("unfreezeall", "nothing was unfrozen")]
    public async Task A_project_whose_agents_cannot_be_read_is_said_to_have_had_nothing_done_and_never_nothing_to_do(string action, string said)
    {
        // A failed read came back as no agents, and the page read no agents as "every agent is already frozen": a person
        // freezing everything before a restart was told so while nobody was frozen (review, lens 2 run 1 on 0.64.4).
        using var console = Everyone();

        var after = await ConfirmAsync(console, action, RoadmapConsole.Project, [], failsOn: "DescribeRegistry");

        Assert.Contains(said, after, StringComparison.Ordinal);
        Assert.Contains("could not be read", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing to", after, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Frozen);
        Assert.Empty(console.Broker.Unfrozen);
    }

    [Theory]
    [InlineData("freezeall", "Operators/Freeze", "0 of 1 agent frozen")]
    [InlineData("unfreezeall", "Operators/Unfreeze", "0 of 1 agent unfrozen")]
    public async Task Agents_the_broker_would_not_freeze_are_counted_as_not_done(string action, string failsOn, string said)
    {
        // The count was of what was tried, so two refusals read "2 agents frozen".
        using var console = Everyone();

        var after = await ConfirmAsync(console, action, RoadmapConsole.Project, [], failsOn: failsOn);

        Assert.Contains(said, after, StringComparison.Ordinal);
        Assert.Contains("1 was not", after, StringComparison.Ordinal);

        // Named, so two agents that failed alike do not read as one sentence said twice.
        Assert.Contains(action == "freezeall" ? $"'{Orders}': " : $"'{Billing}': ", after, StringComparison.Ordinal);
        Assert.Contains("not done", after, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("stopping", ": changed; its node was asked to stop it once its turn ends.")]
    [InlineData("open_outside", ": changed; it is open outside Parley, so its process stays until somebody closes it, but nothing will start it again.")]
    [InlineData("left_running", ": changed; a node that is gone left its process running, so it runs until you end it with Kill, but nothing will start it again.")]
    [InlineData("no_node", ": changed; no node is joined to stop it, so the freeze stands when one returns.")]
    [InlineData("not_running", ": changed.")]
    public async Task Confirming_a_freeze_says_what_became_of_the_agents_process(string process, string said)
    {
        // SPEC.md §10.2: freeze returns what became of the process, and the person is told. A freeze of an agent open in an
        // editor leaves its process up, which only this sentence says. Nothing held the broker's word on its way to the page:
        // with every one read as "not running", or the sentences emptied, every test passed.
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Orders));
        console.Broker.FreezeProcess = process;

        var after = await ConfirmAsync(console, "freeze", Orders, []);

        Assert.Single(console.Broker.Frozen);
        Assert.Contains(RoadmapConsole.Project + said, after, StringComparison.Ordinal);
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
        string query = "",
        string failsOn = "")
    {
        using var client = console.CreateClient();

        var described = await client.GetStringAsync(Address(action, subject, string.Empty), Ct);

        // After the description is read, so it is the act that meets the broker's failure.
        console.Broker.FailsOn = failsOn;

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(described).Groups[1].Value),
            .. fields,
        ]);

        using var response = await client.PostAsync(Address(action, subject, query), form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_frozen_agent_still_running_is_offered_neither_stop_nor_restart_on_its_page()
    {
        // Its node already stops it once its turn ends, and the broker refuses a restart; offered, Stop's words said new work
        // starts it again, which a freeze prevents (review, lens 3 run B on 6257cf80). The terminal leaves both out too.
        using var console = Everyone();
        var row = Row(Orders, frozen: true);
        row.Presence = Wire.AgentPresence.Running;
        console.Broker.Registry.RemoveAll(r => r.Name == Orders);
        console.Broker.Registry.Add(row);

        var page = await AgentPageAsync(console, Orders);

        Assert.DoesNotContain("action=stop&subject=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=restart&subject=", page, StringComparison.Ordinal);
        Assert.Contains("action=unfreeze&subject=", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "end it with Kill")]
    [InlineData(false, "whoever opened it closes it")]
    public async Task The_words_before_a_freeze_promise_no_stop_for_a_process_no_node_holds(bool left, string said)
    {
        using var console = Everyone();
        var row = Row(Orders);
        row.Presence = Wire.AgentPresence.Running;
        row.OpenOutside = true;
        row.Unsupervised = left;
        console.Broker.Registry.RemoveAll(r => r.Name == Orders);
        console.Broker.Registry.Add(row);

        using var client = console.CreateClient();
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(Address("freeze", Orders, string.Empty), Ct));

        Assert.Contains(said, page, StringComparison.Ordinal);
        Assert.DoesNotContain("will be stopped once its current turn ends", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("left_running", "end it with Kill")]
    [InlineData("open_outside", "stays until somebody closes it")]
    public async Task Freezing_everyone_names_an_agent_whose_process_the_freeze_does_not_end(string process, string said)
    {
        // Counted alone, the person was never told a left process runs on until Kill, or an editor's session stays (review,
        // lens 3 run B on fc26cb8a); nor were they promised less than a stop beforehand.
        using var console = new RoadmapConsole();
        var row = Row(Orders);
        row.Presence = Wire.AgentPresence.Running;
        row.OpenOutside = true;
        row.Unsupervised = process == "left_running";
        console.Broker.Registry.Add(row);
        console.Broker.FreezeProcess = process;

        using var client = console.CreateClient();
        var before = WebUtility.HtmlDecode(await client.GetStringAsync(Address("freezeall", RoadmapConsole.Project, string.Empty), Ct));
        var after = await ConfirmAsync(console, "freezeall", RoadmapConsole.Project, []);

        Assert.Contains("keeps running until you end it with Kill", before, StringComparison.Ordinal);
        Assert.Contains(said, after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("one a node holds is stopped once its turn ends")]
    [InlineData("one a node that is gone left running keeps running until you end it with Kill")]
    [InlineData("one open in an editor stays open until whoever opened it closes it")]
    [InlineData("None is started again until unfrozen")]
    public async Task The_words_before_freezing_everyone_say_what_happens_to_each_kind_of_process(string said)
    {
        // Each clause is a promise the person acts on: with any one gone the page still asked and still froze, and the person
        // read a stop, a Kill or an editor left out, or no word that nothing starts them again (review, lens 3 sweep A on
        // 44c8e1e6).
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var before = WebUtility.HtmlDecode(await client.GetStringAsync(Address("freezeall", RoadmapConsole.Project, string.Empty), Ct));

        Assert.Contains(said, before, StringComparison.Ordinal);
    }

    private static async Task<string> AgentPageAsync(RoadmapConsole console, string agent)
    {
        using var client = console.CreateClient();

        return WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Agent?project={RoadmapConsole.Project}&agent={Uri.EscapeDataString(agent)}", UriKind.Relative), Ct));
    }
}
