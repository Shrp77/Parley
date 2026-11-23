using System.Globalization;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That a search with one match more than a page offers that match, with the cursor to fetch it (SPEC.md §10.1).
/// </summary>
/// <remarks>
/// The read counterpart is held by <see cref="HistoryToolTests"/>' one message left over. For a search, only a page and five
/// more was tried, so offering the rest only when more than one was left passed every test, and the one match past a full
/// page was hidden from the agent with nothing to ask for it.
/// </remarks>
[Integration]
public sealed class OneSearchMatchLeftOverIsOfferedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_match_past_a_full_page_is_offered_with_its_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);

        const int Sent = 21;

        for (var i = 0; i < Sent; i++)
        {
            var sent = await docs.RequestAsync("infra@alpha", $"ledger entry {i.ToString(CultureInfo.InvariantCulture)}", cancellationToken: ct);

            Assert.True(sent["exchange_id"]!.GetValue<long>() > 0, sent.ToJsonString());
        }

        var first = await fresh.SearchHistoryAsync("ledger", cancellationToken: ct);

        Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
        Assert.Equal(Sent - 1, first["matches"]!.AsArray().Count);
        Assert.Equal(1, first["not_shown"]!.GetValue<int>());

        var second = await fresh.SearchHistoryAsync("ledger", continue_after: first["continue_after"]!.GetValue<long>(), cancellationToken: ct);

        Assert.Single(second["matches"]!.AsArray());
        Assert.Null(second["not_shown"]);
    }
}
