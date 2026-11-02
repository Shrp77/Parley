using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a Request to the person is always with the person, and never makes a machine try to start anybody (SPEC.md §5,
/// §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists for.</b> Every wake asked a node to start the recipient. No node can start a person, so
/// the answer was <c>UNREACHABLE</c> — and an agent told that about the one participant it had been told to ask when
/// nobody else could help behaves as if its question will never be read, and gives up on the person's decision.
/// </para>
/// <para>
/// <b>Three situations, one answer.</b> No node at all; a node that happens to list a role called
/// <c>human-user</c>, which enrolment refuses but which the wake must not depend on; and the person at a screen, which
/// changes only whether their surface is told something arrived. Watched to fail against 0.35.x: the first two said
/// <c>UNREACHABLE</c>, and the second also sent the node a start.
/// </para>
/// </remarks>
[Integration]
public sealed class MessageToThePersonTests
{
    private static readonly string Person = PersonSeat.On(ProjectName.Create("test")).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_no_machine_connected_the_person_still_has_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var sent = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, Person, "should the refund go to the card or to credit?"),
            cancellationToken: Ct)).Outcomes);

        Assert.True(sent.Ok, sent.Rejection.ToString());
        Assert.Equal(Wire.WakeOutcome.WithThePerson, sent.Wake);
    }

    [Fact]
    public async Task A_node_listing_the_role_is_never_asked_to_start_the_person()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "test", ["human-user"], Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var sent = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, Person, "should the refund go to the card or to credit?"),
            cancellationToken: Ct)).Outcomes);

        Assert.Equal(Wire.WakeOutcome.WithThePerson, sent.Wake);
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task At_a_screen_the_answer_is_the_same_and_the_screen_is_told()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: Ct);

        var sent = Assert.Single((await orders.Client.RequestAsync(
            Requests.Open(orders.Name, Person, "should the refund go to the card or to credit?"),
            cancellationToken: Ct)).Outcomes);

        Assert.Equal(Wire.WakeOutcome.WithThePerson, sent.Wake);
        Assert.NotNull(await seat.WaitForNoticeAsync(TimeSpan.FromSeconds(5)));
    }
}
