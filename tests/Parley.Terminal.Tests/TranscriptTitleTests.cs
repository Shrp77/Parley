using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the transcript box says which conversation it is showing.
/// </summary>
[Integration]
public sealed class TranscriptTitleTests
{
    [Fact]
    public async Task The_box_title_names_the_conversation_and_loses_it_when_the_view_is_left()
    {
        // The owner's call of 2026-10-09. Must fail against a title that is only "Transcript".
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        Assert.Equal($"{TranscriptRegion.RegionTitle} — {Seated.Conversation}", shell.Transcript.Title);

        shell.Transcript.Clear();

        Assert.Equal(TranscriptRegion.RegionTitle, shell.Transcript.Title);
    }
}
