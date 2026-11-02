using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a page reloading itself on a timer does not keep the person marked here (SPEC.md §14).
/// </summary>
/// <remarks>
/// Found by the audit of SPEC.md against the code before the gate resumed. Every render counted as the person being at the
/// console, and four pages reload themselves every thirty or sixty seconds — so a tab left open on the landing page kept
/// the person <c>here</c> for ever, where §14 says a tab nobody is looking at is not somebody at a desk. The reload now
/// carries a marker, and a render carrying it keeps the seat's stream without counting as the person.
/// </remarks>
[Integration]
public sealed class AReloadIsNotAPersonTests
{
    [Fact]
    public async Task A_page_that_reloads_itself_reloads_through_the_marked_address()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("http-equiv=\"refresh\"", body, StringComparison.Ordinal);
        Assert.Contains("reloaded=1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_render_carrying_the_marker_does_not_count_as_the_person()
    {
        // Read from the frame's source, as ListingStateTests reads a redirect: driving it needs a live seat, and what is
        // checked is that the frame asks the question at all.
        var frame = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Web", "Pages", "Shared", "_Layout.cshtml"));

        Assert.Contains("person: !Context.Request.Query.ContainsKey(PersonSeats.Reloaded)", frame, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
