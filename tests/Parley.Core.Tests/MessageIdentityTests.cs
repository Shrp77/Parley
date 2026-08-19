using Xunit;

namespace Parley.Core.Tests;

public sealed class MessageIdentityTests
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");

    private const string Payload = """{"text":"is the invoice total gross or net?"}""";

    [Fact]
    public void Identical_messages_hash_identically()
    {
        Assert.Equal(
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload),
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload));
    }

    [Fact]
    public void The_hash_is_sha256_as_lowercase_hex()
    {
        // The schema constrains the column to exactly this shape, so a change here that the
        // schema rejected would surface as an insert failure rather than a silent mismatch.
        var hash = MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void The_same_text_to_a_different_peer_is_a_different_message()
    {
        // Routing is part of identity. Suppressing this as a duplicate would drop a message
        // its recipient never saw.
        Assert.NotEqual(
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload),
            MessageIdentity.Compute(1, MessageType.Question, Orders, Inventory, Payload));
    }

    [Fact]
    public void The_same_text_on_a_different_exchange_is_a_different_message()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload),
            MessageIdentity.Compute(2, MessageType.Question, Orders, Billing, Payload));
    }

    [Fact]
    public void A_notification_is_distinct_from_the_same_text_on_an_exchange()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(null, MessageType.Notify, Orders, Billing, Payload),
            MessageIdentity.Compute(1, MessageType.Notify, Orders, Billing, Payload));
    }

    [Fact]
    public void The_direction_of_travel_matters()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload),
            MessageIdentity.Compute(1, MessageType.Question, Billing, Orders, Payload));
    }

    [Fact]
    public void The_type_is_part_of_identity()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, Payload),
            MessageIdentity.Compute(1, MessageType.Clarify, Orders, Billing, Payload));
    }

    [Fact]
    public void Concatenation_cannot_be_ambiguous()
    {
        // The same argument PairKey makes. Without an unambiguous separator, fields that run
        // together could produce identical text from different messages: here the payload of
        // one carries what would be the field boundary of the other. Two genuinely different
        // messages must not collide into a single dedup identity.
        var a = MessageIdentity.Compute(1, MessageType.Notify, Orders, Billing, """{"a":""}""");
        var b = MessageIdentity.Compute(1, MessageType.Notify, Orders, Billing, """{"a":"x"}""");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_null_payload_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => MessageIdentity.Compute(1, MessageType.Question, Orders, Billing, null!));
    }

    [Theory]
    [InlineData(MessageType.Question, "question")]
    [InlineData(MessageType.Clarify, "clarify")]
    [InlineData(MessageType.Answer, "answer")]
    [InlineData(MessageType.Notify, "notify")]
    public void Types_round_trip_through_their_stored_tokens(MessageType type, string token)
    {
        // The token feeds both storage and the hash, so renumbering the enum must not silently
        // change what counts as a duplicate.
        Assert.Equal(token, MessageIdentity.FormatType(type));
        Assert.Equal(type, MessageIdentity.ParseType(token));
    }

    [Fact]
    public void An_unrecognised_stored_type_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => MessageIdentity.ParseType("gossip"));
    }

    [Fact]
    public void The_window_is_the_one_the_schema_buckets_by()
    {
        // The generated dedup_bucket column divides by sixty. If these drift apart, the
        // backstop stops backing the rule it exists for.
        Assert.Equal(TimeSpan.FromSeconds(60), MessageIdentity.DeduplicationWindow);
    }
}
