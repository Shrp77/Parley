using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// The new rendering path against input it will actually meet.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.2. <c>Reading</c> is the only genuinely new algorithm this
/// feature added — everything else derives or forwards — and it runs over eight thousand characters
/// of whatever a peer chose to send. Its correctness questions are boundary questions: what does it
/// do with nothing, with only breaks, with a payload that is a scalar rather than an object, with
/// one that nests deeply enough to be a stack.
/// </para>
/// <para>
/// None of these are hypothetical. A payload is validated as JSON by the schema and by nothing
/// else, so every shape JSON permits is a shape this will be handed.
/// </para>
/// </remarks>
public sealed class RenderingEdgeTests
{
    private static Message Message(string payload) =>
        new(
            1,
            1,
            MessageType.Request,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "some-model-9",
            payload,
            "alpha",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            null);

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"intent\":\"\"}")]
    [InlineData("{\"intent\":null}")]
    public void Every_shape_json_permits_produces_a_header_and_does_not_throw(string payload)
    {
        // The header is the invariant: whatever the body turns out to be, a message must be
        // attributable. A rendering that dropped the header on an unexpected payload would make
        // the odd message the one nobody can trace.
        var lines = Reading.Lines([Message(payload)]);

        Assert.Contains(lines, line => line.Kind == LineKind.Header);
        Assert.All(lines, line => Assert.NotNull(line.Text));
    }

    [Fact]
    public void A_payload_that_is_only_line_breaks_does_not_become_a_wall_of_nothing()
    {
        // Blank lines are kept on purpose -- that is what makes paragraphs survive -- so a payload
        // that is nothing but breaks is the case where that choice costs the most.
        var lines = Reading.Lines([Message("""{"intent":"\n\n\n\n\n"}""")]);

        // Bounded by what was sent rather than multiplied by it: five breaks, not fifty lines.
        Assert.True(
            lines.Count(l => l.Kind == LineKind.Body) <= 8,
            $"five line breaks produced {lines.Count(l => l.Kind == LineKind.Body)} body lines");
    }

    [Fact]
    public void A_deeply_nested_payload_is_laid_out_rather_than_refused()
    {
        // Sixty-four levels. Not adversarial in itself; a serialiser with a default depth limit
        // would throw on it, and the whole point of the fallback is that a document this cannot
        // read as one of rule 8.1's shapes is still shown.
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 64))
            + "1"
            + new string('}', 64);

        var lines = Reading.Lines([Message(deep)]);

        Assert.Contains(lines, line => line.Kind == LineKind.Header);
        Assert.Contains(lines, line => line.Text.Contains("\"a\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_payload_the_serialiser_refuses_is_shown_as_it_arrived()
    {
        // Past the default depth limit, where JsonDocument.Parse itself gives up. Showing what is
        // actually there is how somebody finds out the store holds something it should not.
        var tooDeep = string.Concat(Enumerable.Repeat("{\"a\":", 200))
            + "1"
            + new string('}', 200);

        var lines = Reading.Lines([Message(tooDeep)]);

        Assert.Contains(lines, line => line.Kind == LineKind.Header);
        Assert.Contains(lines, line => line.Text.Contains("\"a\"", StringComparison.Ordinal));
    }

    [Fact]
    public void An_eight_thousand_character_body_is_rendered_in_full()
    {
        // The message cap. Nothing here may truncate: the cap is what bounds the input, and a
        // renderer that clipped as well would hide the tail of a message somebody is reading to
        // check something specific.
        var long_ = new string('x', 7_900);
        var lines = Reading.Lines([Message($$"""{"intent":"{{long_}}"}""")]);

        Assert.Contains(lines, line => line.Text.Contains(long_, StringComparison.Ordinal));
    }

    [Fact]
    public void Messages_from_the_same_agent_in_a_row_each_keep_their_own_header()
    {
        // No collapsing of runs. It would read better and it would mean a reader could not tell
        // two messages from one long one, which matters when the second is an answer.
        var lines = Reading.Lines(
            [Message("""{"intent":"one"}"""), Message("""{"intent":"two"}""")]);

        Assert.Equal(2, lines.Count(line => line.Kind == LineKind.Header));
    }

    [Fact]
    public void An_empty_conversation_produces_nothing_rather_than_a_frame()
    {
        Assert.Empty(Reading.Lines([]));
    }

    [Fact]
    public void The_local_time_helper_handles_the_instants_a_store_can_hold()
    {
        // DateTimeOffset's extremes reach this through a timestamp somebody typed into the store
        // by hand, or a clock that was wrong. Converting them must not throw on the way to a
        // screen that is trying to tell somebody what happened.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus-five", TimeSpan.FromHours(5), "test +5", "test +5");

        Assert.NotEmpty(LocalTime.Write(DateTimeOffset.MinValue, zone));
        Assert.NotEmpty(LocalTime.Write(DateTimeOffset.MaxValue, zone));
        Assert.NotEmpty(LocalTime.Write(DateTimeOffset.UnixEpoch, zone));
    }
}
