using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

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
                Drawn.Rows(region.List).Where(AtColumnZero),
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

        foreach (var row in Drawn.Rows(region.List).Where(AtColumnZero))
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

        Assert.DoesNotContain(Drawn.Rows(region.List).Where(AtColumnZero), row => row.Length > 0);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, twice over. The transcript has to have wrapped at all -- a payload that fit
        // on one row would never reach the code this is about -- and something has to be at column
        // zero, or "nothing forged is at column zero" is true of an empty screen.
        var region = Region($"{{\"intent\":\"{new string('x', 76)} {Forged}\"}}");
        var rows = Drawn.Rows(region.List);

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


    [Fact]
    public void The_rule_holds_at_every_width_a_terminal_can_be()
    {
        // **Swept rather than sampled.** The indent is applied by `Redraw`, which wraps to the
        // width less the indent -- so the interesting widths are the ones where that arithmetic
        // is degenerate, and a test at one comfortable width would never reach them.
        //
        // Checked as "is the start of a line this surface wrote" rather than by pattern, because
        // at four columns a header itself wraps to single characters and a rule looking for `->`
        // would call those a violation. What FR-018 is about is where a line begins.
        var region = Region($"{{\"intent\":\"{Said}\"}}");
        var problems = new List<string>();

        // **Read off the widest render rather than written down.** The first version of this
        // hard-coded the header text, which embeds a time rendered in the machine's own zone --
        // so it passed on a developer's machine in Eastern time and failed in a UTC container at
        // widths seven to fourteen, naming pieces of `1970-01-01` as violations. That is the same
        // class of trap as the CRLF one the spec suite carries a note about: a check that passes
        // on one machine and not another is not checking what it claims.
        region.Resize(400);

        var headers = Drawn.Rows(region.List).Where(AtColumnZero).ToList();

        Assert.NotEmpty(headers);

        for (var width = 1; width <= 200; width++)
        {
            region.Resize(width);

            var rows = Drawn.Rows(region.List);

            problems.AddRange(rows
                .Where(AtColumnZero)
                .Where(row => !headers.Any(header => header.StartsWith(row, StringComparison.Ordinal)))
                .Select(row => $"width {width}: '{row}' begins at column zero"));

            // And nothing is dropped on the way: every non-space character of the payload is still
            // somewhere on the screen, at every width.
            var drawn = string.Concat(rows.Select(Squeezed));

            if (!drawn.Contains(Squeezed(Said), StringComparison.Ordinal))
            {
                problems.Add($"width {width}: the payload did not survive the wrap");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(8)));
    }

    /// <summary>A message long enough to wrap at every width worth testing.</summary>
    private const string Said =
        "a decision about the cap with a very-long-identifier-nobody-can-break-sensibly "
        + "and some ordinary words after it";

    /// <summary>The same text with every space taken out, for comparing across wraps.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its non-space characters.</returns>
    private static string Squeezed(string text) =>
        text.Replace(" ", string.Empty, StringComparison.Ordinal);

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

}
