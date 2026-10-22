using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Taking delivery on arriving at a conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>It was a key to press, which a person met as a count that never went down</b>, and the key the status line named
/// answered on two panes only. Opening a conversation takes delivery now and there is no key (the owner's calls of
/// 2026-10-08), so this holds that arrival does it.
/// </para>
/// </remarks>
public sealed class TakingDeliveryTests
{
    private const string Conversation = "room";

    [Fact]
    public async Task Opening_a_conversation_takes_what_is_waiting()
    {
        // Must fail against delivery being only a key: the message would still be waiting after the open.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        await seated.AsksAsync(Seated.Orders, seated.Person, "Which file validates the licence key?", Conversation, ct);

        Assert.Equal(1, (await seated.Shell.Voice()!.WaitingAsync(ct)).Messages);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, Conversation);
        await seated.Shell.Settled;

        Assert.Equal(0, (await seated.Shell.Voice()!.WaitingAsync(ct)).Messages);
    }
}
