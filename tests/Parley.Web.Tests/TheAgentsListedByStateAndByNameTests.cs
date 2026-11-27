using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The agents page as drawn: grouped by state, each state a section in the list's order with its heading and how many it
/// holds; A to Z, every agent in name order with who is working said above them; and the toggle marking the view shown.
/// </summary>
/// <remarks>
/// <b>Feature 042, the person's feedback.</b> <see cref="AgentsByStateTests"/> holds that sections exist and that A to Z has
/// none. Found by review: the sections' order, headings and counts, the A to Z order, the line saying who is working, the
/// toggle's mark and what the address must say for A to Z could each be changed and every test passed.
/// </remarks>
[Integration]
public sealed partial class TheAgentsListedByStateAndByNameTests
{
    private const string Machine = "pc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task By_state_each_state_is_one_section_in_the_lists_order_with_its_heading_and_count()
    {
        var sections = Sections(await PageAsync("/?tab=agents&handler=AgentTable"));

        // Working and background work rank first and are taken in the legend's order, then stopped, then running and open
        // outside Parley in the legend's order, then not running: never the order the agents' names happen to give.
        Assert.Equal(
            [
                "◆ Working on a turn, 1 agent: orders",
                "◈ Background work running, 1 agent: billing",
                "■ Stopped, still owes work, 1 agent: dispatch",
                "● Running, 1 agent: shipping",
                "◇ Open outside Parley, 1 agent: inventory",
                "○ Not running, 2 agents: ledger refunds",
            ],
            sections);
    }

    [Fact]
    public async Task A_to_Z_lists_every_agent_in_name_order_and_says_who_is_working()
    {
        var lines = await PageAsync("/?tab=agents&by=name&handler=AgentTable");

        Assert.Equal(["billing", "dispatch", "inventory", "ledger", "orders", "refunds", "shipping"], Agents(lines));
        Assert.Equal("orders", Working(lines));
    }

    [Fact]
    public async Task By_state_does_not_say_who_is_working_above_the_sections()
    {
        var lines = await PageAsync("/?tab=agents&handler=AgentTable");

        Assert.DoesNotContain("data-working", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("Working now:", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_toggle_marks_the_view_shown()
    {
        Assert.Equal("By state", MarkedView(await PageAsync("/?tab=agents")));
        Assert.Equal("A–Z", MarkedView(await PageAsync("/?tab=agents&by=name")));
    }

    [Fact]
    public async Task Any_other_word_in_the_address_is_the_view_by_state()
    {
        Assert.Equal("By state", MarkedView(await PageAsync("/?tab=agents&by=other")));

        var lines = await PageAsync("/?tab=agents&by=other&handler=AgentTable");

        Assert.Equal(6, Sections(lines).Count);
        Assert.DoesNotContain("data-working", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_to_Z_lists_one_role_on_two_machines_by_machine_whichever_is_busier()
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;

        // The one on pc is working, so by state it would come first; A to Z, laptop comes before pc.
        console.Broker.Registry.Add(Row("orders", Wire.AgentPresence.Running, row =>
        {
            row.Name = "orders@pc";
            row.TurnRunningSinceUtc = Utc(now.AddMinutes(-2));
        }));
        console.Broker.Registry.Add(Row("orders", Wire.AgentPresence.Startable, row => row.Name = "orders@laptop"));
        using var client = console.CreateClient();

        var lines = await client.GetStringAsync(new Uri("/?tab=agents&by=name&handler=AgentTable", UriKind.Relative), Ct);

        Assert.Equal(
            ["orders@laptop", "orders@pc"],
            Regex.Matches(lines, "data-agent=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
    }

    [GeneratedRegex("<details class=\"state-group\" open data-state-group=\"([^\"]+)\">")]
    private static partial Regex SectionStart();

    [GeneratedRegex("<span class=\"state-group-name\">([^<]*)</span><span class=\"count\">(\\d+)<span class=\"visually-hidden\"> (agents?)</span>")]
    private static partial Regex SectionHead();

    [GeneratedRegex("data-agent=\"([^\"@]+)@")]
    private static partial Regex AgentLine();

    [GeneratedRegex("<p class=\"dgrid-empty\" data-working role=\"status\"><strong>Working now:</strong>([^<]*)</p>")]
    private static partial Regex WorkingLine();

    [GeneratedRegex("<a href=\"[^\"]*\" aria-current=\"page\">([^<]*)</a>")]
    private static partial Regex MarkedLink();

    private static async Task<string> PageAsync(string path)
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;

        // One agent in each state the list draws, and two not running, so a count of more than one is drawn. Named so that
        // within a rank the name order is not the legend's, and A to Z is not the list's own order.
        console.Broker.Registry.Add(Row("orders", Wire.AgentPresence.Running, row => row.TurnRunningSinceUtc = Utc(now.AddMinutes(-2))));
        console.Broker.Registry.Add(Row("billing", Wire.AgentPresence.Running, row => row.BackgroundTasks = 1));
        console.Broker.Registry.Add(Row("dispatch", Wire.AgentPresence.Startable, row =>
        {
            row.LastTurnEndedUtc = Utc(now.AddMinutes(-5));
            row.LastTurnPermissionDenials = 1;
        }));
        console.Broker.Registry.Add(Row("shipping", Wire.AgentPresence.Running, row => row.LastTurnEndedUtc = Utc(now.AddMinutes(-1))));
        console.Broker.Registry.Add(Row("inventory", Wire.AgentPresence.Running, row => row.OpenOutside = true));
        console.Broker.Registry.Add(Row("refunds", Wire.AgentPresence.Startable, _ => { }));
        console.Broker.Registry.Add(Row("ledger", Wire.AgentPresence.Startable, _ => { }));

        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
    }

    private static Wire.RegistryRow Row(string role, Wire.AgentPresence presence, Action<Wire.RegistryRow> state)
    {
        var row = new Wire.RegistryRow
        {
            Name = role + "@" + Machine,
            Adapter = "claude-code",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
            Presence = presence,
            Connected = presence == Wire.AgentPresence.Running,
            TurnsReported = true,
        };

        state(row);

        return row;
    }

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Each section as drawn, in order: its mark, heading and count, then the agents in it.</summary>
    private static List<string> Sections(string lines)
    {
        var starts = SectionStart().Matches(lines);
        var sections = new List<string>();

        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : lines.Length;
            var section = lines[from..to];
            var head = SectionHead().Match(section);

            Assert.True(head.Success, $"section {starts[i].Groups[1].Value} has no heading and count");
            sections.Add(
                $"{WebUtility.HtmlDecode(starts[i].Groups[1].Value)} {WebUtility.HtmlDecode(head.Groups[1].Value)}, "
                + $"{head.Groups[2].Value} {head.Groups[3].Value}: {string.Join(' ', Agents(section))}");
        }

        return sections;
    }

    private static string[] Agents(string drawn) => [.. AgentLine().Matches(drawn).Select(m => m.Groups[1].Value)];

    private static string Working(string lines)
    {
        var working = WorkingLine().Matches(lines);

        Assert.Single(working);
        return WebUtility.HtmlDecode(working[0].Groups[1].Value).Trim();
    }

    private static string MarkedView(string page)
    {
        var start = page.IndexOf("<nav class=\"view-toggle\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the page has no toggle between the two views");

        var toggle = page[start..page.IndexOf("</nav>", start, StringComparison.Ordinal)];
        var marked = MarkedLink().Matches(toggle);

        Assert.Single(marked);
        return WebUtility.HtmlDecode(marked[0].Groups[1].Value);
    }
}
