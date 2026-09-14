using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a person holding a Request with no item a Result can name is still offered a way to answer it.
/// </summary>
/// <remarks>
/// Found by the review gate, lens 1 run 1.15. The terminal and the console build their Result form from
/// <see cref="Delivered.Items"/>, and a question 0.4 wrote whose only id is too long for a Result had none: the terminal
/// said it carried no items and offered nothing, while the broker would have accepted a Result for it as a whole.
/// </remarks>
public sealed class WholeRequestTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static Delivered Of(MessageType type, string payload) =>
        new(new Message(1, 1, type, Orders, Billing, "claude-opus-5", payload, "pricing", T0, T0));

    [Fact]
    public void A_request_whose_only_id_is_too_long_is_answered_as_a_whole()
    {
        var over = new string('b', MessageLimits.LongestItemId + 1);

        Assert.Equal(
            [PayloadShape.WholeRequest],
            Of(MessageType.Request, $$"""{"intent":"x","sub_questions":[{"id":"{{over}}","text":"gross or net?"}]}""").Items());
    }

    [Fact]
    public void A_request_with_items_offers_those_and_nothing_else()
    {
        Assert.Equal(
            ["q1"],
            Of(MessageType.Request, """{"intent":"x","items":[{"id":"q1","kind":"question","text":"gross or net?"}],"done_when":"the price is stated as gross or net"}""").Items());
    }

    [Fact]
    public void Nothing_but_a_request_owes_items()
    {
        Assert.Empty(Of(MessageType.Clarify, """{"text":"of VAT?"}""").Items());
    }
}
