using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That every surface names Parley as Parley, a person's reply as a person's, and an automatic line as one nobody read
/// (SPEC.md principle 2.11, FR-003a, FR-005, FR-018).
/// </summary>
/// <remarks>
/// <b>Both surfaces draw from these, so this is where the words are held.</b> The terminal's transcript and the web UI's
/// both call <see cref="Speaker"/>; both reviews call <see cref="Provenance"/>. Each case was watched to fail with the
/// attribution read from the registry name instead.
/// </remarks>
public sealed class ParleyIsNamedForWhatItIsTests
{
    private static readonly AgentName Parley = Facilitation.On("test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static Message From(AgentName from, AgentName to, string responder) =>
        new(1, 1, MessageType.Clarify, from, to, responder, """{"text":"hello"}""", "owns-orders-1",
            DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void Parleys_own_question_reads_as_Parleys()
    {
        var question = From(Parley, Orders, Facilitation.Attribution);

        Assert.Equal(Facilitation.DisplayName, Speaker.From(question));
        Assert.Equal("orders@test", Speaker.To(question));
    }

    [Fact]
    public void A_persons_reply_through_Parley_never_reads_as_Parleys()
    {
        // Same registry name as the line above; only the attribution differs, and it is the whole of the difference.
        var reply = From(Parley, Orders, Facilitation.PersonAttribution);

        Assert.Equal(Speaker.PersonThroughParley, Speaker.From(reply));
        Assert.NotEqual(Facilitation.DisplayName, Speaker.From(reply));
    }

    [Fact]
    public void An_answer_to_Parley_names_Parley_as_its_recipient()
    {
        Assert.Equal(Facilitation.DisplayName, Speaker.To(From(Orders, Parley, "some-model-9")));
        Assert.Equal("orders@test", Speaker.From(From(Orders, Parley, "some-model-9")));
    }

    [Fact]
    public void An_automatic_line_is_said_to_be_one_nobody_read_and_differs_from_every_person_route()
    {
        var automatic = Provenance.Of(OwnershipSource.Automatic);

        Assert.Contains("no person has read", automatic, StringComparison.Ordinal);
        Assert.NotEqual(automatic, Provenance.Of(OwnershipSource.Proposed));
        Assert.NotEqual(automatic, Provenance.Of(OwnershipSource.Edited));
        Assert.NotEqual(automatic, Provenance.Of(OwnershipSource.Composed));
        Assert.NotEqual(automatic, Provenance.Of((OwnershipSource?)null));
    }
}
