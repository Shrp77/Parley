using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a person holding a Request is offered a way to answer it, as a whole.
/// </summary>
/// <remarks>
/// The terminal and the console build their Result form from <see cref="Delivered.Items"/>, and a Request declares
/// no identifiers (SPEC.md §5.1), so the form answers it under <see cref="PayloadShape.WholeRequest"/>.
/// </remarks>
public sealed class WholeRequestTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static Delivered Of(MessageType type, string payload) =>
        new(new Message(1, 1, type, Orders, Billing, "claude-opus-5", payload, "pricing", T0, T0));

    [Fact]
    public void A_request_is_answered_as_a_whole()
    {
        Assert.Equal(
            [PayloadShape.WholeRequest],
            Of(MessageType.Request, """{"ask":"gross or net?"}""").Items());
    }

    [Fact]
    public void Nothing_but_a_request_owes_items()
    {
        Assert.Empty(Of(MessageType.Clarify, """{"text":"of VAT?"}""").Items());
    }
}
