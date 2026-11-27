using Xunit;

namespace Parley.Core.Tests;

[Unit]
public sealed class MessageIdentityTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");

    private const string Payload = """{"text":"is the invoice total gross or net?"}""";

    [Fact]
    public void Identical_messages_hash_identically()
    {
        Assert.Equal(
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload));
    }

    [Fact]
    public void The_hash_is_sha256_as_lowercase_hex()
    {
        // The schema constrains the column to exactly this shape, so a change here that the
        // schema rejected would surface as an insert failure rather than a silent mismatch.
        var hash = MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void The_same_text_to_a_different_peer_is_a_different_message()
    {
        // Routing is part of identity. Suppressing this as a duplicate would drop a message
        // its recipient never saw.
        Assert.NotEqual(
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Inventory, Payload));
    }

    [Fact]
    public void The_same_text_on_a_different_exchange_is_a_different_message()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.Compute(2, 0, MessageType.Request, Orders, Billing, Payload));
    }

    [Fact]
    public void The_same_text_later_in_the_same_exchange_is_a_different_message()
    {
        // A requester answering "yes" to two clarifications inside the window. Without the version these
        // were one identity, so the second reply was declined as a duplicate, reported as sent, and the
        // turn never moved (SPEC.md §13.1).
        Assert.NotEqual(
            MessageIdentity.Compute(1, 2, MessageType.Clarify, Orders, Billing, Payload),
            MessageIdentity.Compute(1, 4, MessageType.Clarify, Orders, Billing, Payload));
    }

    [Fact]
    public void A_request_identity_ignores_the_exchange_it_will_open()
    {
        // Written as the exchange is created, when there is no identifier yet. Folding one in would make
        // every hash unique and switch off the distinctness index of §7.1.
        Assert.Equal(
            MessageIdentity.Compute(null, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.ComputeRequest(Orders, Billing, Payload));

        Assert.NotEqual(
            MessageIdentity.ComputeRequest(Orders, Billing, Payload),
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload));
    }

    [Fact]
    public void The_direction_of_travel_matters()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.Compute(1, 0, MessageType.Request, Billing, Orders, Payload));
    }

    [Fact]
    public void The_type_is_part_of_identity()
    {
        Assert.NotEqual(
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, Payload),
            MessageIdentity.Compute(1, 0, MessageType.Clarify, Orders, Billing, Payload));
    }

    [Fact]
    public void Concatenation_cannot_be_ambiguous()
    {
        // The same argument PairKey makes. Without an unambiguous separator, fields that run
        // together could produce identical text from different messages: here the payload of
        // one carries what would be the field boundary of the other. Two genuinely different
        // messages must not collide into a single dedup identity.
        var a = MessageIdentity.Compute(1, 0, MessageType.Result, Orders, Billing, """{"a":""}""");
        var b = MessageIdentity.Compute(1, 0, MessageType.Result, Orders, Billing, """{"a":"x"}""");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_null_payload_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, null!));
    }

    [Theory]
    [InlineData(MessageType.Request, "request")]
    [InlineData(MessageType.Clarify, "clarify")]
    [InlineData(MessageType.Result, "result")]
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
    public void The_canonical_form_is_pinned()
    {
        // A golden value. SPEC 13.1 names SHA-256 and the fields, but not the byte-level
        // canonical form, and the digest depends on it entirely: reorder the fields, change
        // the separator, or format a null exchange differently and every hash already stored
        // becomes unreachable. Deduplication would then stop matching history without any
        // test failing and without the column shape changing. It also fixes the value an
        // independent implementation must reproduce to share a database.
        //
        // It moved once, when names became role@machine (SPEC.md 4.1.1), and that is the
        // failure this test is for working correctly rather than a reason to distrust it: the
        // names are inside the hashed form, so every stored hash became unreachable. The store
        // was rebuilt from empty in the same change, so nothing was stranded -- had it not
        // been, this failing is what would have said so.
        //
        Assert.Equal(
            "252fabab45718e0aba7f178b7f5ca460a57dc24c2d23946637c0186d8f98e4de",
            MessageIdentity.Compute(1, 0, MessageType.Request, Orders, Billing, "{}"));
    }

    [Fact]
    public void The_window_is_the_one_the_schema_buckets_by()
    {
        // The generated dedup_bucket column divides by sixty. If these drift apart, the
        // backstop stops backing the rule it exists for.
        Assert.Equal(TimeSpan.FromSeconds(60), MessageIdentity.DeduplicationWindow);
    }
}
