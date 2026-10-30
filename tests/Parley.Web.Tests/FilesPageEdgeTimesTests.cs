using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The Files page still renders a file whose times the broker sent empty, malformed or at the edges of the range.
/// </summary>
/// <remarks>
/// Review gate, lens 2 run 2.5. A time the client cannot read becomes the earliest instant there is, and the page writes
/// every time in the reader's own zone; converting that instant to a zone behind UTC is the step that could fail.
/// </remarks>
[Integration]
public sealed class FilesPageEdgeTimesTests : IDisposable
{
    private readonly FilesConsole _console = new();

    public void Dispose() => _console.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("not a time")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    public async Task A_file_whose_times_the_broker_sent_malformed_or_at_the_edges_still_renders_the_page(string stamp)
    {
        _console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "pricing",
            Folder = "pricing",
            Name = "notes.md",
            SizeBytes = 3,
            Sha256 = new string('a', 64),
            PlacedBy = "billing@alpha",
            PlacedUtc = stamp
        });

        using var client = _console.CreateClient();
        using var response = await client.GetAsync(new Uri("/Projects/Files?project=" + FilesConsole.Project, UriKind.Relative), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} for '{stamp}'");
        Assert.Contains("notes.md", body, StringComparison.Ordinal);
    }
}
