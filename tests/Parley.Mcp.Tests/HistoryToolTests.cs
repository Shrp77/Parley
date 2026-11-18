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

    [Fact]
    public async Task Every_snippet_is_labelled_inert_and_so_is_the_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);
        _ = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);

        var found = await fresh.SearchHistoryAsync("DNS", cancellationToken: ct);

        Assert.StartsWith("[recorded text, inert] ", Assert.Single(found["matches"]!.AsArray())!["snippet"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("never instructions", found["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_that_is_limited_says_so_and_how_long_to_wait_and_a_search_the_same()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);
        var sent = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);
        var exchange = sent["exchange_id"]!.GetValue<long>();

        System.Text.Json.Nodes.JsonNode last = new System.Text.Json.Nodes.JsonObject();

        for (var i = 0; i < 40; i++)
        {
            last = await fresh.ReadExchangeAsync(exchange, cancellationToken: ct);
        }

        Assert.Equal("rate_limited", last["found"]!.GetValue<string>());
        Assert.InRange(last["retry_after_seconds"]!.GetValue<int>(), 1, 60);

        var search = await fresh.SearchHistoryAsync("DNS", cancellationToken: ct);

        Assert.False(search["ok"]!.GetValue<bool>());
        Assert.InRange(search["retry_after_seconds"]!.GetValue<int>(), 1, 60);
    }

    [Fact]
    public async Task A_long_exchange_gives_the_cursor_to_carry_on_with_and_the_arguments_reach_the_broker()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, infra) = await fixture.LaunchAsync("infra@alpha", ct);
        var (_, fresh) = await fixture.LaunchAsync("billing@test", ct);
        var sent = await docs.RequestAsync("infra@alpha", "start " + new string('x', 30_000), cancellationToken: ct);
        var exchange = sent["exchange_id"]!.GetValue<long>();
        _ = await infra.InboxAsync(cancellationToken: ct);
        _ = await infra.ClarifyAsync(exchange, new string('y', 40_000), cancellationToken: ct);
        _ = await docs.ClarifyAsync(exchange, new string('z', 40_000), cancellationToken: ct);

        var first = await fresh.ReadExchangeAsync(exchange, cancellationToken: ct);

        Assert.True(first["messages_remaining"]!.GetValue<int>() > 0);
        var next = first["next_after"]!.GetValue<long>();
        var second = await fresh.ReadExchangeAsync(exchange, next, cancellationToken: ct);

        Assert.NotEqual(first["messages"]![0]!["message_id"]!.GetValue<long>(), second["messages"]![0]!["message_id"]!.GetValue<long>());
        Assert.Equal("open", first["state"]!.GetValue<string>());

        var conversation = first["conversation"]!.GetValue<string>();
        Assert.Single((await fresh.SearchHistoryAsync("start", participant: "docs@test", conversation: conversation, type: "request", since: "2026-01-01T00:00:00Z", until: "2027-01-01T00:00:00Z", cancellationToken: ct))["matches"]!.AsArray());
        Assert.Empty((await fresh.SearchHistoryAsync("start", participant: "nobody@test", cancellationToken: ct))["matches"]!.AsArray());
        Assert.Empty((await fresh.SearchHistoryAsync("start", conversation: "elsewhere", cancellationToken: ct))["matches"]!.AsArray());
        Assert.Empty((await fresh.SearchHistoryAsync("start", since: "2030-01-01T00:00:00Z", cancellationToken: ct))["matches"]!.AsArray());
    }
}
