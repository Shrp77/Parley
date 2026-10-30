using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That an agent collecting an answer to its request to a role is told how many agents it reached and how many have
/// answered (SPEC.md §5.5), and that an answer to a request for one agent says nothing of the kind.
/// </summary>
public sealed class RoleRequestProgressToolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_answer_to_a_request_to_a_role_says_how_it_stands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, alpha) = await fixture.LaunchAsync("infra@alpha", ct);
        _ = await fixture.LaunchAsync("infra@beta", ct);

        var sent = await docs.RequestAsync("infra", "What does this machine believe about DNS?", cancellationToken: ct);
        Assert.Equal(2, sent["expect_results"]!.GetValue<int>());
        var toAlpha = sent["sent"]!.AsArray().Single(s => s!["agent"]!.GetValue<string>() == "infra@alpha")!["exchange_id"]!.GetValue<long>();

        _ = await alpha.InboxAsync(cancellationToken: ct);
        _ = await alpha.ResultAsync(toAlpha, "answered", "It resolves through the office router.", cancellationToken: ct);

        var answer = Assert.Single((await docs.InboxAsync(cancellationToken: ct))["messages"]!.AsArray())!;
        var role = answer["your_request_to_the_role"]!;

        Assert.Equal(2, role["asked"]!.GetValue<int>());
        Assert.Equal(1, role["answered"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_blocked_answer_counts_as_an_answer()
    {
        // SPEC.md §5.5: an answer is an exchange a Result closed, complete or blocked (lens 3 run 86: the store's count of
        // blocked was held by nothing).
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, alpha) = await fixture.LaunchAsync("infra@alpha", ct);
        _ = await fixture.LaunchAsync("infra@beta", ct);

        var sent = await docs.RequestAsync("infra", "What does this machine believe about DNS?", cancellationToken: ct);
        var toAlpha = sent["sent"]!.AsArray().Single(s => s!["agent"]!.GetValue<string>() == "infra@alpha")!["exchange_id"]!.GetValue<long>();

        _ = await alpha.InboxAsync(cancellationToken: ct);
        var blocked = await alpha.ResultAsync(toAlpha, "blocked", "I cannot read the resolver's settings.", blocker: "permission", cancellationToken: ct);
        Assert.True(blocked["ok"]!.GetValue<bool>(), blocked.ToJsonString());

        var answer = Assert.Single((await docs.InboxAsync(cancellationToken: ct))["messages"]!.AsArray())!;

        Assert.Equal(1, answer["your_request_to_the_role"]!["answered"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_answer_to_a_request_for_one_agent_says_nothing_of_a_role()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        var (_, alpha) = await fixture.LaunchAsync("infra@alpha", ct);

        var sent = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);
        _ = await alpha.InboxAsync(cancellationToken: ct);
        _ = await alpha.ResultAsync(sent["exchange_id"]!.GetValue<long>(), "answered", "Through the router.", cancellationToken: ct);

        var answer = Assert.Single((await docs.InboxAsync(cancellationToken: ct))["messages"]!.AsArray())!;

        Assert.Null(answer["your_request_to_the_role"]);
    }
}
