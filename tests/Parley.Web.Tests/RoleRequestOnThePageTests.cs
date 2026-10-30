using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page says how many of the agents a Request to a role reached have answered (SPEC.md §5.5).
/// </summary>
/// <remarks>Lens 3 run 86 found the page's line held by nothing; the terminal's came through the shared layout.</remarks>
public sealed class RoleRequestOnThePageTests
{
    [Fact]
    public async Task The_page_says_how_many_answered_beneath_the_request()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, "docs@alpha", "infra@alpha", "What does this machine believe about DNS?");
        console.Broker.Request(2, "docs@alpha", "infra@beta", "What does this machine believe about DNS?");
        console.Broker.Exchanges[0].RequestGroup = "g";
        console.Broker.Exchanges[0].State = Wire.ExchangeState.Closed;
        console.Broker.Exchanges[0].ClosedUtc = "2026-09-16T10:05:00Z";
        console.Broker.Exchanges[0].Outcome = Wire.ExchangeOutcome.Complete;
        console.Broker.Exchanges[1].RequestGroup = "g";

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("Sent to every agent in role infra (2): 1 of 2 answered.", page, StringComparison.Ordinal);
    }
}
