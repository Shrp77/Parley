using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page's export and files links encode the values they carry.
/// </summary>
/// <remarks>
/// Lens 2 found the links built by pasting the query values into the address, so an identifier holding <c>&amp;</c>
/// or <c>=</c> added a parameter of its own to the link instead of being part of the identifier.
/// </remarks>
[Integration]
public sealed class ExportLinksEncodeTheirValuesTests
{
    [Fact]
    public async Task An_identifier_with_query_characters_stays_one_value_in_every_link()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(5, "orders@test", "billing@test", "Add CSV export");
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        var html = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={TranscriptConsole.Project}&id=a%26handler%3DRaw", UriKind.Relative),
            token);

        Assert.Contains("id=a%26handler%3DRaw&amp;handler=Raw\"", html, StringComparison.Ordinal);
        Assert.Contains("id=a%26handler%3DRaw&amp;handler=Transcript\"", html, StringComparison.Ordinal);
        Assert.Contains("conversation=a%26handler%3DRaw\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=a&amp;handler=Raw", html, StringComparison.Ordinal);
    }
}
