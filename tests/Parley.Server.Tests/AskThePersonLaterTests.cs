using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent can ask the person at any point in a long session and get the person's answer (SPEC.md principle
/// 2.6, SC-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's words are the specification</b>: "They need to be able to ask the user what they want to do even if
/// it's 7 hours into a marathon session." So the same exchange is driven after four waits on a fake clock — a minute,
/// the twenty-minute idle window, an hour, seven hours — and each must be admitted, told "with the person", wait
/// uncollected, and reach the agent as the person's words once answered.
/// </para>
/// <para>
/// <b>No retention case here, deliberately.</b> The owner's call (the first <c>/speckit-analyze</c>, issue 8) is that
/// the person's mail follows the same rules as everybody's (FR-010), and those already never delete a message in open
/// work.
/// </para>
/// </remarks>
public sealed class AskThePersonLaterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Person = PersonSeat.On(ProjectName.Create("test")).Value;

    public static TheoryData<int> Minutes() => new() { 1, 20, 60, 7 * 60 };

    [Theory]
    [MemberData(nameof(Minutes))]
    public async Task The_person_answers_however_long_the_agent_waited(int minutes)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new StubClock(T0);

        await using var broker = await LoopbackBroker.StartAsync(ct, clock: clock);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: ct);

        var asked = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, Person, "should the refund go to the card or to credit?"),
            cancellationToken: ct)).Outcomes);

        Assert.True(asked.Ok, asked.Rejection.ToString());
        Assert.Equal(Wire.WakeOutcome.WithThePerson, asked.Wake);

        clock.Now = T0.AddMinutes(minutes);

        // The person comes back: their surface attaches as the seat and collects what waited for them.
        await using var seat = await AttachedClient.AttachAsync(
            broker, Person, cancellationToken: ct, adapter: PersonSeat.Adapter);

        var waited = Assert.Single((await seat.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = Person }, cancellationToken: ct)).Messages);

        Assert.Equal(asked.ExchangeId, waited.Message.ExchangeId);

        var reply = Requests.Result(Person, asked.ExchangeId);
        reply.ResponderModel = "human:human-user";

        var answered = await seat.Client.ResultAsync(reply, cancellationToken: ct);

        Assert.True(answered.Ok, answered.Refusal.ToString());

        var result = Assert.Single((await orders.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: ct)).Messages);

        Assert.Equal(Person, result.Message.From);
        Assert.Equal("human:human-user", result.Message.ResponderModel);
    }
}
