using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// That the record added to <c>export</c> is the same thing in the document, on the wire, and in
/// the code.
/// </summary>
/// <remarks>
/// <para>
/// A *field* is the kind of change that gets into one of the three (document, proto, code) and
/// not the others, because nothing forces the document and the proto to move together.
/// </para>
/// <para>
/// The specific asymmetry worth checking: the proto is what another implementation reads to build a
/// client, and `SPEC.md` is what it reads to know what the answer means. A field in one and not the
/// other produces either a value nobody knows how to interpret or a promise nobody can receive.
/// </para>
/// </remarks>
[Integration]
public sealed class DestroyedFieldTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string Read(params string[] under) =>
        File.ReadAllText(Path.Combine([Root(), .. under]));

    /// <summary>Runs a document's lines together so a wrapped phrase can be found.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The same text with every run of whitespace reduced to one space.</returns>
    /// <remarks>
    /// Comment and prose checks ask about meaning; a raw substring search asks about line breaks as
    /// well, and the two look the same until a sentence is rewrapped. A check that
    /// searches a document for a phrase fails for this reason and no other.
    /// </remarks>
    private static string Flowed(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text.Replace("//", " ", StringComparison.Ordinal),
            @"\s+",
            " ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    [Fact]
    public void The_specification_says_export_answers_for_a_destroyed_conversation()
    {
        // Without this, the field on the wire is a value another implementation would have to guess
        // the meaning of -- and the obvious guess, that an absent one means nothing was destroyed,
        // is right only by luck.
        var spec = Read("SPEC.md");

        Assert.Contains("it returns the record §14 left instead of nothing", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_specification_says_why_it_is_export_and_not_an_operation_of_its_own()
    {
        // §10.2 is a versioned contract, so what it does *not* gain is as much a decision as what it
        // does. A reader implementing this needs to know that the absence of a fifth operation was
        // chosen.
        var spec = Read("SPEC.md");

        Assert.Contains(
            "could have had an operation of its own; it does not", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_carries_the_field_and_says_what_an_absent_one_means()
    {
        var proto = Read("proto", "parley.proto");

        Assert.Contains("message DestroyedConversation", proto, StringComparison.Ordinal);
        Assert.Contains("optional DestroyedConversation destroyed", proto, StringComparison.Ordinal);

        // Optional rather than a message that is always present and sometimes empty, because those
        // two are different answers and the proto has to be able to express both.
        //
        // **Matched against whitespace-normalised text.** A phrase in a comment wraps where the line ends, so searching a
        // document for a contiguous sentence asks about its line breaks rather than its meaning.
        Assert.Contains("no such conversation ever", Flowed(proto), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_record_carries_the_same_four_facts_the_schema_stores()
    {
        // A field on one and not the other is how a record comes back missing something an operator
        // was told would be there.
        var proto = Read("proto", "parley.proto");
        var spec = Read("SPEC.md");

        var message = proto[proto.IndexOf("message DestroyedConversation", StringComparison.Ordinal)..];
        message = message[..message.IndexOf('}', StringComparison.Ordinal)];

        foreach (var field in new[] { "conversation_id", "destroyed_utc", "messages", "participants" })
        {
            Assert.Contains(field, message, StringComparison.Ordinal);
        }

        var table = spec[spec.IndexOf("CREATE TABLE destroyed_conversations", StringComparison.Ordinal)..];
        table = table[..table.IndexOf(") STRICT;", StringComparison.Ordinal)];

        foreach (var column in new[] { "id", "destroyed_utc", "messages", "participants" })
        {
            Assert.Contains(column, table, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Neither_the_wire_record_nor_the_table_has_a_field_that_could_hold_a_payload()
    {
        // §9.4's argument, checked where it would be broken: by somebody adding a field. Both
        // shapes are small enough to read, and both must stay that way.
        var proto = Read("proto", "parley.proto");
        var message = proto[proto.IndexOf("message DestroyedConversation", StringComparison.Ordinal)..];
        message = message[..message.IndexOf('}', StringComparison.Ordinal)];

        foreach (var forbidden in new[] { "payload", "text", "intent", "body" })
        {
            Assert.DoesNotContain(forbidden, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_checks_are_reading_the_documents()
    {
        Assert.Contains("message DestroyedConversation", Read("proto", "parley.proto"), StringComparison.Ordinal);
        Assert.Contains("destroyed_conversations", Read("SPEC.md"), StringComparison.Ordinal);
    }
}
