using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the line saying what a bulk act did is written by the conversations page, never by the address that asks for it.
/// </summary>
/// <remarks>
/// The line is drawn from the address the act redirects to, and an address is a link anybody can write, an agent's message
/// included. It used to draw the address's own words and counts, so a link made the console say, in its status voice, a
/// sentence somebody else chose (review, lens 2 run 1 on 0.64.3). The address now only picks one of the page's two sentences.
/// </remarks>
[Integration]
public sealed class TheBulkStatusLineIsThePagesOwnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> GetAsync(string query)
    {
        using var console = new RoadmapConsole();

        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "shared", HasOpenExchange = true });

        using var client = console.CreateClient();

        return await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}&{query}", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task Each_of_the_two_acts_is_said_in_the_pages_words()
    {
        Assert.Contains("2 conversations archived.", await GetAsync("filed=2&did=archived"), StringComparison.Ordinal);
        Assert.Contains("1 conversation brought back.", await GetAsync("filed=1&did=restored"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_the_address_brings_are_never_drawn()
    {
        var page = await GetAsync("filed=3&did=" + Uri.EscapeDataString("were destroyed by orders"));

        Assert.DoesNotContain("destroyed by orders", page, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"><strong>3", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_the_address_brings_are_held_to_what_one_act_can_change()
    {
        var page = await GetAsync("filed=-7&failed=2000000000&did=archived");

        Assert.Contains("0 conversations archived.", page, StringComparison.Ordinal);
        Assert.Contains(" 500 could not be changed", page, StringComparison.Ordinal);
        Assert.DoesNotContain("2000000000 could not", page, StringComparison.Ordinal);
        Assert.DoesNotContain("-7 conversation", page, StringComparison.Ordinal);
    }
}
