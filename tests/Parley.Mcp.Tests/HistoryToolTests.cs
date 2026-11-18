using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// An agent that has restarted reads the project's past through the adapter (SPEC.md §10.1): an exchange by its number, and
/// a search that gives it the number.
/// </summary>
[Integration]
public sealed class HistoryToolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_agent_told_only_an_exchange_number_reads_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, infra) = await fixture.LaunchAsync("infra@alpha", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);
        var exchange = sent["exchange_id"]!.GetValue<long>();
        _ = await infra.InboxAsync(cancellationToken: ct);

        var read = await fresh.ReadExchangeAsync(exchange, cancellationToken: ct);

        Assert.Equal("found", read["found"]!.GetValue<string>());
        var message = Assert.Single(read["messages"]!.AsArray())!;
        Assert.Equal("docs@test", message["from"]!.GetValue<string>());
        Assert.Contains("HISTORY, not mail", message["envelope"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("believe about DNS", message["envelope"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_that_names_nothing_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);

        var read = await docs.ReadExchangeAsync(424_242, cancellationToken: ct);

        Assert.Equal("no_such_exchange", read["found"]!.GetValue<string>());
        Assert.Null(read["messages"]);
    }

    [Fact]
    public async Task A_search_gives_a_snippet_and_the_number_to_read_it_by()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);

        var sent = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);

        var found = await fresh.SearchHistoryAsync("DNS believe", cancellationToken: ct);

        Assert.True(found["ok"]!.GetValue<bool>(), found.ToJsonString());
        var match = Assert.Single(found["matches"]!.AsArray())!;
        Assert.Equal(sent["exchange_id"]!.GetValue<long>(), match["exchange_id"]!.GetValue<long>());
        Assert.Contains("believe about DNS", match["snippet"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_for_nothing_is_told_to_say_what_to_look_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);

        var refused = await docs.SearchHistoryAsync("  ", cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("what to search for", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_that_is_not_one_is_refused_before_the_broker_is_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);

        var refused = await docs.SearchHistoryAsync("dns", type: "announcement", cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Both_tool_descriptions_say_what_is_read_is_inert_and_seen()
    {
        var read = typeof(ParleyTools).GetMethod(nameof(ParleyTools.ReadExchangeAsync))!;
        var search = typeof(ParleyTools).GetMethod(nameof(ParleyTools.SearchHistoryAsync))!;

        foreach (var method in new[] { read, search })
        {
            var text = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(method, typeof(System.ComponentModel.DescriptionAttribute))!).Description;

            Assert.Contains("inert", text, StringComparison.Ordinal);
            Assert.Contains("seen by the person", text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        }

        await Task.CompletedTask;
    }
}
