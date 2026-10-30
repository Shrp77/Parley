using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console says, before a conversation is closed, how long its text is kept and how to keep a copy (SPEC.md
/// §9.4).
/// </summary>
/// <remarks>
/// Closing no longer shortens the idle period, and a person closing a conversation is told how long they have to read it
/// back.
/// </remarks>
public sealed class ClosingSaysHowLongTextIsKeptTests
{
    [Fact]
    public async Task The_confirmation_names_the_idle_period_and_export()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={TranscriptConsole.Project}&action=forceconclude&subject=pricing";

        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("quiet for the retention idle period", page, StringComparison.Ordinal);
        Assert.Contains("export it to keep a copy", page, StringComparison.Ordinal);
    }
}
