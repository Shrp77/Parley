using Parley.Client.Rendering;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page lists every agent of the project and marks the ones that took part, as the terminal's
/// room does (specs/026).
/// </summary>
/// <remarks>
/// Both surfaces read <see cref="RoomRoster"/> — one rule for who is offered — so they agree by construction; the room's
/// drawn rows are pinned by the terminal's tests and this pins the page: the others are listed, can be ticked as
/// recipients, and a retired agent and the person's seat are not among them.
/// </remarks>
[Integration]
public sealed class RoomOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Inventory = "inventory@test";
    private const string Ledger = "ledger@test";
    private const string Archive = "archive@test";

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static Wire.RegistryRow Row(string name, Wire.AgentStatus status = Wire.AgentStatus.Active, string adapter = "claude-code") =>
        new()
        {
            Name = name,
            Adapter = adapter,
            Tier = Wire.AgentTier.Full,
            Status = status,
            Presence = Wire.AgentPresence.Running,
        };

    private static TranscriptConsole Project()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");

        console.Broker.Registry.AddRange(
        [
            Row(Orders), Row(Billing), Row(Inventory), Row(Ledger),
            Row(Archive, Wire.AgentStatus.Retired),
            Row("human-user@alpha", adapter: Parley.Core.PersonSeat.Adapter),
            Row("parley-facilitator@alpha", adapter: "parley-facilitator"),
        ]);

        return console;
    }

    [Fact]
    public async Task The_agents_that_have_not_taken_part_are_listed_and_can_be_ticked_as_recipients()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("Not in it yet", page, StringComparison.Ordinal);

        foreach (var agent in new[] { Inventory, Ledger })
        {
            Assert.Contains($"name=\"Targets\" value=\"{agent}\"", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_participants_carry_the_mark_and_the_legend_says_what_it_means()
    {
        using var console = Project();
        using var client = console.CreateClient();

        // Decoded, because Razor writes a non-ASCII character as a numeric reference and a browser reads it as the character.
        var page = System.Net.WebUtility.HtmlDecode(
            await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken));

        Assert.Contains(RoomRoster.ParticipantMark, page, StringComparison.Ordinal);
        Assert.Contains(RoomRoster.ParticipantLegend, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_agent_the_persons_seat_and_Parley_are_not_offered()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain($"value=\"{Archive}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"human-user@alpha\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"parley-facilitator@alpha\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_whose_agents_all_took_part_offers_nobody_else()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        console.Broker.Registry.AddRange([Row(Orders), Row(Billing)]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Not in it yet", page, StringComparison.Ordinal);
    }
}
