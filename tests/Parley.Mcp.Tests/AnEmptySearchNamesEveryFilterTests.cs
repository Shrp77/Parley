using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>That an empty search says the filters may be why whichever filter the agent gave, not only a participant.</summary>
/// <remarks>
/// <see cref="TheHistoryToolsDescribeEveryArgumentTests"/> filtered only by participant, the first filter the check reads,
/// so a check that looked at that one alone passed it; an agent that narrowed by time or kind and found nothing was told
/// to change its words (review, lens 3 surface A on d99806d9).
/// </remarks>
[Integration]
public sealed class AnEmptySearchNamesEveryFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("conversation")]
    [InlineData("type")]
    [InlineData("since")]
    [InlineData("until")]
    public async Task An_empty_search_with_one_filter_names_the_filters(string filter)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);
        var (_, reader) = await fixture.LaunchAsync("billing@test", ct);

        _ = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);

        var reply = filter switch
        {
            "conversation" => await reader.SearchHistoryAsync("DNS", conversation: "no-such-conversation", cancellationToken: ct),
            "type" => await reader.SearchHistoryAsync("DNS", type: "clarify", cancellationToken: ct),
            "since" => await reader.SearchHistoryAsync("DNS", since: "2030-01-01", cancellationToken: ct),
            _ => await reader.SearchHistoryAsync("DNS", until: "2020-01-01", cancellationToken: ct),
        };

        var said = reply.ToJsonString();

        Assert.Contains("Nothing matched", said, StringComparison.Ordinal);
        Assert.Contains("filters", said, StringComparison.Ordinal);
    }
}
