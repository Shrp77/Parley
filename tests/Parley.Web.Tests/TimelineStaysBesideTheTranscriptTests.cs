using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the minimap stays in the column beside the transcript when a tick is chosen.</summary>
/// <remarks>
/// The conversation is a two-column grid. The note that says whose messages a chosen tick marks is hidden while empty,
/// and when it was a grid cell of its own, filling it moved it into the second column and pushed the rail onto a new
/// row below the whole transcript: to a reader, the timeline vanished. The note and the rail now share one box, and the
/// grid has exactly two children, so nothing the script writes can move the rail. The script is not run here; what is
/// held is the structure it cannot break.
/// </remarks>
[Integration]
public sealed class TimelineStaysBesideTheTranscriptTests
{
    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    [Fact]
    public async Task The_note_and_the_rail_are_one_box_after_the_transcript()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, "orders@test", "billing@test", "Which rate applies?");

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var shape = new Regex(
            """</ol>\s*(?:@?\*[\s\S]*?\*@?\s*)?<div class="minimap">\s*<p id="kin"[^>]*></p>\s*<nav class="timeline"[\s\S]*?</nav>\s*</div>\s*</div>""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.Matches(shape, page);
        Assert.Single(Regex.Matches(page, "id=\"kin\"", RegexOptions.None, TimeSpan.FromSeconds(5)));
    }
}
