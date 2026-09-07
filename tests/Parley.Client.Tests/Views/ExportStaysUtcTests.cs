using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That making the screen show local time did not make the export show it too.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two artefacts have different readers and that is the whole argument.</b> A transcript on
/// screen is read by the person who opened it, on the machine they are sitting at, so UTC makes
/// them do arithmetic. A file <c>parley export</c> wrote is read by somebody else, somewhere else,
/// possibly months later — and a file saying <c>14:03</c> without saying whose is worse than one
/// saying <c>14:03Z</c>.
/// </para>
/// <para>
/// <b>This test exists because the obvious refactor breaks it.</b> "Make the transcript writer
/// format times nicely" is a reasonable-sounding change that would silently make every export
/// relative to whichever machine produced it.
/// </para>
/// </remarks>
public sealed class ExportStaysUtcTests
{
    private static Message Message(int hourUtc) =>
        new(
            1,
            1,
            MessageType.Question,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "some-model-9",
            """{"intent":"a question"}""",
            "alpha",
            new DateTimeOffset(2026, 1, 1, hourUtc, 0, 0, TimeSpan.Zero),
            null);

    [Fact]
    public void The_export_writes_the_stored_instant_and_marks_it()
    {
        var written = Transcript.Write("alpha", "some-project", [Message(12)], []);

        // "u" format: the trailing Z is what tells a later reader whose noon this was.
        Assert.Contains("2026-01-01 12:00:00Z", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_carries_no_local_rendering()
    {
        var written = Transcript.Write("alpha", "some-project", [Message(12)], []);

        // The screen's format, which must not appear here. Asserted as the exact shape rather than
        // as "no local time", because a negative that cannot name what it is looking for passes
        // against anything.
        Assert.DoesNotContain("2026-01-01 12:00:00  ", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_and_the_export_deliberately_disagree()
    {
        // Rendered through a zone the test names, so this asserts a conversion happened rather than
        // that two values agree on whichever machine the suite runs on.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus-five", TimeSpan.FromHours(5), "test +5", "test +5");

        var screen = Reading.Lines([Message(12)], null, zone)
            .First(l => l.Kind == LineKind.Header).Text;

        var export = Transcript.Write("alpha", "some-project", [Message(12)], []);

        Assert.Contains("17:00:00", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("17:00:00", export, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_helper_is_what_the_screen_uses()
    {
        // Both surfaces call one helper so they cannot write a local time differently. If this
        // stopped being true the two would drift apart one release at a time.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus-five", TimeSpan.FromHours(5), "test +5", "test +5");

        var header = Reading.Lines([Message(12)], null, zone)
            .First(l => l.Kind == LineKind.Header).Text;

        Assert.StartsWith(
            LocalTime.Write(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), zone),
            header,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_names_the_zone_so_a_screen_full_of_times_is_not_ambiguous()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus-five", TimeSpan.FromHours(5), "test +5", "test +5");

        Assert.Contains("test +5", LocalTime.Note(zone), StringComparison.Ordinal);
    }
}
