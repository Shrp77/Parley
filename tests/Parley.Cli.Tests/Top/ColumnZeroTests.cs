using Parley.Cli.Top;
using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That nothing but a header reaches column zero of a transcript, whatever an agent sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-018's rule, and the reason it is structural rather than coloured.</b> A header sits at
/// column zero and nothing else does, so a reader can tell who said something from the shape of
/// the screen alone — which is the property the whole suite is held to, because every other test
/// here reads the screen with its attributes stripped.
/// </para>
/// <para>
/// <b>It was not true.</b> <see cref="TranscriptRegion.Wrap"/> trims the leading space off each
/// piece it breaks off, and that space is the indent the rule is made of. A payload is
/// agent-written and eight thousand characters long, so its author chooses where the break lands:
/// seventy-six filler characters and then a plausible header produced a row reading
/// <c>billing@here -&gt; orders@here (Answer)</c>, at column zero, in a conversation
/// <c>billing@here</c> had never sent a message to.
/// </para>
/// <para>
/// Colour distinguished them — a body line keeps the terminal's own foreground and a header takes
/// its sender's — which is precisely the crutch the rule exists so as not to need.
/// </para>
/// </remarks>
public sealed class ColumnZeroTests
{
    /// <summary>A line shaped exactly like one this surface writes.</summary>
    private const string Forged = "1970-01-01 00:00:00  billing@here -> orders@here  (Answer)";

    [Fact]
    public void A_payload_aimed_at_the_wrap_cannot_put_a_header_at_column_zero()
    {
        // Aimed rather than hoped for. The region wraps to its width less the indent, so filler
        // that fills the first row exactly puts whatever follows at the start of a continuation --
        // which is the piece the wrap used to left-trim.
        foreach (var filler in Enumerable.Range(60, 30))
        {
            var region = Region($"{{\"intent\":\"{new string('x', filler)} {Forged}\"}}");

            Assert.DoesNotContain(
                Rows(region).Where(AtColumnZero),
                row => row.Contains("billing@here", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_row_at_column_zero_is_a_header_this_surface_wrote()
    {
        // The rule itself rather than one attack on it. Anything at column zero must be one of the
        // two things this surface puts there: a message header, or the heading naming the
        // conversation.
        var region = Region(
            $"{{\"said\":\"{new string('y', 200)}\",\"intent\":\"{new string('z', 300)} {Forged}\"}}");

        foreach (var row in Rows(region).Where(AtColumnZero))
        {
            var header = row.Contains("orders@here -> inventory@here", StringComparison.Ordinal)
                || row.StartsWith("Conversation ", StringComparison.Ordinal);

            Assert.True(header, $"'{row}' sits at column zero and is not a header");
        }
    }

    [Fact]
    public void A_broker_that_chooses_its_own_error_text_cannot_reach_column_zero_either()
    {
        // The other string this region shows without an agent between it and the screen: whatever
        // came back from the far end of a gRPC call.
        var region = new TranscriptRegion();

        region.Show("threeway", "alpha", null, Forged);

        Assert.DoesNotContain(Rows(region).Where(AtColumnZero), row => row.Length > 0);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, twice over. The transcript has to have wrapped at all -- a payload that fit
        // on one row would never reach the code this is about -- and something has to be at column
        // zero, or "nothing forged is at column zero" is true of an empty screen.
        var region = Region($"{{\"intent\":\"{new string('x', 76)} {Forged}\"}}");
        var rows = Rows(region);

        Assert.Contains(rows, row => row.StartsWith(' '));
        Assert.Contains(rows, AtColumnZero);
        Assert.Contains(rows, row => row.Contains("billing@here", StringComparison.Ordinal));
    }

    [Fact]
    public void The_indent_is_the_one_the_shared_renderer_uses()
    {
        // Four, from Reading, rather than a number chosen here. Two conventions that look alike is
        // how somebody comes to trust the wrong line, which is the argument Reading.Indent already
        // carries about the export's own two-space annotations.
        Assert.Equal(4, Reading.Indent);
    }

    /// <summary>Whether a row begins at column zero.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="true"/> when it has no leading space.</returns>
    private static bool AtColumnZero(string row) => row.Length > 0 && row[0] != ' ';

    /// <summary>A transcript of one message carrying a payload.</summary>
    /// <param name="payload">What the sender put in it.</param>
    /// <returns>The region, drawn.</returns>
    private static TranscriptRegion Region(string payload)
    {
        var region = new TranscriptRegion();

        region.Show(
            "threeway",
            "alpha",
            new ConversationExport(
                [
                    new Message(
                        1,
                        ExchangeId: null,
                        MessageType.Question,
                        AgentName.Create("orders@here"),
                        AgentName.Create("inventory@here"),
                        "test",
                        payload,
                        "threeway",
                        DateTimeOffset.UnixEpoch,
                        null),
                ],
                []),
            null);

        return region;
    }

    /// <summary>Everything the region is showing, in order.</summary>
    /// <param name="region">The region.</param>
    /// <returns>The rows.</returns>
    private static List<string> Rows(TranscriptRegion region)
    {
        var source = region.List.Source;

        return [.. Enumerable
            .Range(0, source?.Count ?? 0)
            .Select(at => source!.ToList()[at]?.ToString() ?? string.Empty)];
    }
}
