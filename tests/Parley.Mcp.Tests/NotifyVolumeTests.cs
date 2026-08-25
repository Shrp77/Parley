using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What currently bounds Notify, which is very little.
/// </summary>
/// <remarks>
/// <para>
/// These record an absence rather than a guarantee, and they are written to fail when it is
/// filled. SPEC.md §17 question 1 asks what bounds Notify; §5.4 exempts it from the pair cap of
/// §7.1 and from the cycle check of §7.2, leaving §13.1's deduplication as its only guard — and
/// §13 named per-sender rate limits beside it that have never existed.
/// </para>
/// <para>
/// So the assertions below say "nothing refuses this" on purpose. A rate limit landing is the
/// intended way for them to break: whoever adds one should find these, see the number that used
/// to get through, and update §17 question 1 rather than discovering later that the document
/// still describes the deployment as unbounded.
/// </para>
/// </remarks>
public sealed class NotifyVolumeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Deduplication_catches_a_repeat_and_reports_it_as_sent()
    {
        // The guard that does exist, and the shape of its success. A repeat is not an error —
        // the message was delivered, once — so the second call reports success and stores
        // nothing. Pinned because a future rate limit must not be mistaken for this.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        const string Event = "The OrderLine contract gained a PriceSnapshot field.";

        var first = await orders.NotifyAsync("billing", Event, cancellationToken: ct);
        var second = await orders.NotifyAsync("billing", Event, cancellationToken: ct);

        Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
        Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());

        var inbox = await billing.InboxAsync(cancellationToken: ct);

        Assert.Single(inbox["messages"]!.AsArray());
    }

    [Fact]
    public async Task One_character_of_difference_defeats_it_entirely()
    {
        // Not a defect in deduplication, which is doing exactly its job: these are different
        // messages. It is the reason deduplication cannot be the volume control §13 implied it
        // was part of, because the natural output of a model summarising events is a stream of
        // messages that differ.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        for (var i = 0; i < 30; i++)
        {
            var sent = await orders.NotifyAsync(
                "billing",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"The OrderLine contract gained a PriceSnapshot field. Revision {i}."),
                cancellationToken: ct);

            Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());
        }

        var inbox = await billing.InboxAsync(cancellationToken: ct);

        // Thirty accepted from one peer, with nothing refusing any of them. If this ever fails,
        // a bound has arrived and §17 question 1 needs rewriting to say what it is.
        Assert.Equal(30, inbox["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task An_unbounded_drain_delivers_everything_at_once()
    {
        // The other half, and the one that turns volume into a single turn's problem. `inbox`
        // takes an optional limit and defaults to no limit, so the whole backlog arrives in one
        // tool result — which is where §7.1's reasoning about a context flood applies, through a
        // door §7.1 does not watch.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        for (var i = 0; i < 20; i++)
        {
            await orders.NotifyAsync(
                "billing",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Event {i}: the contract moved again."),
                cancellationToken: ct);
        }

        var all = await billing.InboxAsync(cancellationToken: ct);

        Assert.Equal(20, all["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_recipient_can_bound_its_own_drain_which_is_the_only_control_it_has()
    {
        // Worth pinning because it is the whole of the recipient's defence today. The sender is
        // unbounded; the receiver may choose to collect fewer at a time, and the rest stay
        // pending rather than being discarded.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        for (var i = 0; i < 10; i++)
        {
            await orders.NotifyAsync(
                "billing",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Event {i}."),
                cancellationToken: ct);
        }

        var first = await billing.InboxAsync(limit: 3, cancellationToken: ct);
        Assert.Equal(3, first["messages"]!.AsArray().Count);

        var rest = await billing.InboxAsync(cancellationToken: ct);
        Assert.Equal(7, rest["messages"]!.AsArray().Count);
    }
}
