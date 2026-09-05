using Parley.Cli.Top;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That a transcript survives being made narrower and wider again.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.10. The transcript is the only region that lays its content out
/// rather than listing rows, and it does so against whatever width it currently has — so it is
/// re-laid on every viewport change. What must survive is the content: a reflow that rebuilt from
/// the wrapped lines rather than from the text would lose a word at every width change, and would
/// lose it silently.
/// </para>
/// </remarks>
public sealed class ReflowTests
{
    private const string Said = "orders@test asked billing@test whether the totals include tax";

    [Fact]
    public void The_text_is_the_same_at_every_width()
    {
        // Rebuilt from the written transcript each time, so narrowing and widening is not a lossy
        // round trip. The failure this prevents is a region that wrapped its own output: the second
        // wrap would break the first one's lines again and the third would break those.
        var region = Region();

        var wide = region.Written;

        foreach (var width in new[] { 30, 20, 10, 20, 30, 120 })
        {
            region.Resize(width);

            Assert.Equal(wide, region.Written);
        }
    }

    [Fact]
    public void Nothing_is_lost_from_what_is_drawn_at_any_width()
    {
        var region = Region();

        foreach (var width in new[] { 120, 40, 20 })
        {
            var drawn = string.Join(
                " ",
                region.Written.ReplaceLineEndings("\n").Split('\n')
                    .SelectMany(line => TranscriptRegion.Wrap(line, width)));

            Assert.Contains("orders@test", drawn, StringComparison.Ordinal);
            Assert.Contains("billing@test", drawn, StringComparison.Ordinal);
            Assert.Contains("tax", drawn, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Closing_and_opening_again_shows_the_second_conversation_rather_than_the_first()
    {
        // The region is reused across conversations, so what it holds has to be replaced rather
        // than added to. A transcript that accumulated would show two conversations as one, which
        // is the worst kind of wrong on a screen whose whole point is who said what.
        var region = Region();

        Assert.Contains("tax", region.Written, StringComparison.Ordinal);

        region.Clear();

        Assert.DoesNotContain("tax", region.Written, StringComparison.Ordinal);
        Assert.Null(region.Open);

        region.Show("second", "alpha", Export("something else entirely"), null);

        Assert.Contains("something else entirely", region.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("tax", region.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_could_not_be_asked_replaces_the_transcript_rather_than_joining_it()
    {
        var region = Region();

        region.Show("conv", "alpha", null, "not answering");

        Assert.DoesNotContain("tax", region.Written, StringComparison.Ordinal);
        Assert.Contains("not answering", region.Written, StringComparison.Ordinal);
        Assert.Null(region.Open);
    }

    private static TranscriptRegion Region()
    {
        var region = new TranscriptRegion();

        region.Show("conv", "alpha", Export(Said), null);

        return region;
    }

    private static ConversationExport Export(string said) =>
        new(
            [
                new Message(
                    1,
                    null,
                    MessageType.Notify,
                    AgentName.Create("orders@test"),
                    AgentName.Create("billing@test"),
                    "test",
                    "{\"said\":\"" + said + "\"}",
                    "conv",
                    DateTimeOffset.UnixEpoch,
                    null),
            ],
            []);
}
