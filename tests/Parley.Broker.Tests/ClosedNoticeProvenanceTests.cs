using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a Request is carried back only to the agent that wrote it.
/// </summary>
/// <remarks>
/// Found by the review gate, lens 2. Every delivered Result carried its Request back under "This is the Result of a
/// request you sent. Your request, as you sent it" — including the closed Result a person's close writes to the
/// <i>receiver</i>, which was then shown the requester's words as its own. The delimiters held; the label did not,
/// and a peer's text presented as the agent's own is exactly the provenance §13 exists to keep straight.
/// </remarks>
public sealed class ClosedNoticeProvenanceTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly Broker _broker;

    public ClosedNoticeProvenanceTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_receivers_closed_notice_does_not_claim_it_sent_the_request()
    {
        await _broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), "pricing", Ct);
        await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.True(await new OperatorConsole(_connection).ForceConcludeAsync("pricing", "nobody needs it", T0.AddMinutes(3), Ct));

        var toReceiver = Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(4), cancellationToken: Ct)).Messages);
        var toRequester = Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(4), cancellationToken: Ct)).Messages);

        Assert.DoesNotContain("as you sent it", toReceiver.Envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("begin your request", toReceiver.Envelope, StringComparison.Ordinal);

        // The requester still gets its own words back, which is what §4.2 carries them for.
        Assert.Contains("as you sent it", toRequester.Envelope, StringComparison.Ordinal);
        Assert.Contains("begin your request", toRequester.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closing_notice_is_the_brokers_and_names_no_peer_as_its_author()
    {
        // Found by the review gate, lens 2 run 2.7. The notice sits on the exchange, so it carries billing as its
        // sender, and it was rendered "Message from billing ... another participant in your project" around the
        // person's own reason for closing. Billing wrote none of it.
        await _broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), "pricing", Ct);

        Assert.True(await new OperatorConsole(_connection).ForceConcludeAsync("pricing", "the pricing change was dropped", T0.AddMinutes(2), Ct));

        var notice = Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: Ct)).Messages);

        Assert.Contains("Notice from the Parley broker about your exchange with `billing@test`", notice.Envelope, StringComparison.Ordinal);
        Assert.Contains("`billing@test` did not write it", notice.Envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("Message from `billing@test`", notice.Envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("another participant in your project", notice.Envelope, StringComparison.Ordinal);
        Assert.Contains("the pricing change was dropped", notice.Envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_a_peer_wrote_is_still_its_message()
    {
        var sent = await _broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), "pricing", Ct);
        await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.True((await _broker.ResultAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct)).Succeeded);

        var result = Assert.Single((await _broker.DrainInboxAsync(Orders, T0.AddMinutes(4), cancellationToken: Ct)).Messages);

        Assert.Contains("Message from `billing@test`", result.Envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("Notice from the Parley broker", result.Envelope, StringComparison.Ordinal);
    }
}
