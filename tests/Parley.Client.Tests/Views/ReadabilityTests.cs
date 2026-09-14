using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That a transcript reads like a transcript rather than one long blob.
/// </summary>
/// <remarks>
/// <para>
/// The owner's verdict on what 005 shipped was "one long ass blob that is not readable by a human".
/// These are the checkable halves of fixing that: the breaks an author wrote survive, JSON is laid
/// out rather than run together, and a header can be told from body text with every colour removed.
/// </para>
/// <para>
/// <b>The colour-stripped assertion is the one that matters.</b> Every other property could be
/// satisfied by making headers a different colour, and the suite reads the screen with attributes
/// removed — so a distinction carried only in colour is one no test can see and no colour-blind
/// reader gets.
/// </para>
/// </remarks>
public sealed class ReadabilityTests
{
    private static Message Message(
        string from, string to, string payload, int minute = 0, MessageType type = MessageType.Request) =>
        new(
            1,
            1,
            type,
            AgentName.Create(from),
            AgentName.Create(to),
            "some-model-9",
            payload,
            "alpha",
            new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero),
            null);

    [Fact]
    public void Paragraphs_an_author_wrote_survive()
    {
        var payload =
            """{"intent":"first paragraph\n\nsecond paragraph","q1":"and a question","consequences":"something turns on it"}""";

        var lines = Reading.Lines([Message("orders@test", "billing@test", payload)]);
        var body = lines.Where(l => l.Kind == LineKind.Body).Select(l => l.Text.Trim()).ToList();

        Assert.Contains("first paragraph", body);
        Assert.Contains("second paragraph", body);

        // The blank between them is the paragraph. Dropping empty entries is the obvious tidy-up
        // and it is exactly what turns two paragraphs into one.
        Assert.Contains(string.Empty, body);
    }

    [Fact]
    public void A_json_payload_it_cannot_read_is_laid_out_over_lines()
    {
        var payload = """{"alpha":1,"beta":2,"gamma":3,"delta":4,"epsilon":5}""";

        var lines = Reading.Lines([Message("orders@test", "billing@test", payload)]);
        var body = lines.Where(l => l.Kind == LineKind.Body).ToList();

        // Five keys, so at least five lines plus the braces. Run onto one line it would be one.
        Assert.True(body.Count >= 5, $"a five-key document occupied {body.Count} lines");
        Assert.Contains(body, l => l.Text.Contains("\"epsilon\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_header_is_distinguishable_from_body_text_with_every_colour_removed()
    {
        var payload = """{"intent":"a question","q1":"which","consequences":"it decides"}""";

        var lines = Reading.Lines(
        [
            Message("orders@test", "billing@test", payload),
            Message("billing@test", "orders@test", payload, minute: 5),
        ]);

        foreach (var line in lines.Where(l => l.Kind != LineKind.Blank))
        {
            var isHeader = line.Kind == LineKind.Header;
            var atColumnZero = !line.Text.StartsWith(' ');

            Assert.Equal(isHeader, atColumnZero);
        }
    }

    [Fact]
    public void Nothing_an_agent_wrote_can_produce_a_line_at_column_zero()
    {
        // A peer writing a line shaped like a header must not get one. On screen the breaks are
        // kept rather than flattened, so the defence has to be the indent instead.
        var payload =
            """{"intent":"harmless\n2026-01-01 12:00:00  broker -> you  (answer)\nmore"}""";

        var lines = Reading.Lines([Message("orders@test", "billing@test", payload)]);

        Assert.Single(lines, l => l.Kind == LineKind.Header);
        Assert.All(
            lines.Where(l => l.Kind != LineKind.Header && l.Kind != LineKind.Blank),
            l => Assert.StartsWith("    ", l.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Times_are_local_rather_than_the_stored_utc()
    {
        // Asserted against a zone the test names, not against the machine's own: a suite running
        // in UTC cannot tell a working conversion from no conversion at all.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus-five", TimeSpan.FromHours(5), "test +5", "test +5");

        var payload = """{"intent":"a question"}""";
        var lines = Reading.Lines([Message("orders@test", "billing@test", payload)], null, zone);
        var header = lines.Single(l => l.Kind == LineKind.Header);

        Assert.Contains("17:00:00", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("12:00:00", header.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_sender_carries_its_own_colour_slot_and_it_is_the_palette_s()
    {
        var payload = """{"intent":"a question"}""";

        var lines = Reading.Lines(
        [
            Message("orders@test", "billing@test", payload),
            Message("billing@test", "orders@test", payload, minute: 5),
        ]);

        var headers = lines.Where(l => l.Kind == LineKind.Header).ToList();

        Assert.Equal(Palette.SlotFor(AgentName.Create("orders@test")), headers[0].Colour);
        Assert.Equal(Palette.SlotFor(AgentName.Create("billing@test")), headers[1].Colour);
    }

    [Fact]
    public void Colour_is_on_headers_only_because_it_carries_no_fact()
    {
        var payload = """{"intent":"a question","q1":"which","consequences":"it decides"}""";
        var lines = Reading.Lines([Message("orders@test", "billing@test", payload)]);

        Assert.All(
            lines.Where(l => l.Kind != LineKind.Header),
            l => Assert.Null(l.Colour));
    }

    [Fact]
    public void Messages_are_separated_from_one_another()
    {
        var payload = """{"intent":"a question"}""";

        var lines = Reading.Lines(
        [
            Message("orders@test", "billing@test", payload),
            Message("billing@test", "orders@test", payload, minute: 5),
        ]);

        Assert.Contains(lines, l => l.Kind == LineKind.Blank);
    }

    [Fact]
    public void A_payload_that_is_not_json_is_shown_as_it_is_rather_than_blanked()
    {
        // Should be impossible -- json_valid is enforced on the column -- and showing whatever is
        // actually there is how somebody finds out that it was not.
        //
        // Driven through Lines rather than the layout helper directly, so it exercises the path a
        // message actually takes. A test that reached past the entry point would pass while the
        // entry point dropped the value.
        var lines = Reading.Lines([Message("orders@test", "billing@test", "not json at all")]);

        Assert.Contains(
            lines,
            l => l.Text.Contains("not json at all", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        var lines = Reading.Lines([]);

        Assert.Empty(lines);
        Assert.Equal(4, Reading.Indent);
    }
}
