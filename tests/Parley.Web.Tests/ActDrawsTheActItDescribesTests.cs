using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the Act page draws its fields for the same action its description and its handler read.</summary>
/// <remarks>
/// The page compared the action ignoring case while the description and the act lower-case it, and those differ for a Kelvin
/// sign (U+212A lower-cases to <c>k</c>, but does not equal <c>K</c> ignoring case): one spelling was described as one act and
/// drawn as another.
/// </remarks>
[Integration]
public sealed class ActDrawsTheActItDescribesTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reply_spelled_with_a_kelvin_sign_is_drawn_as_the_reply_it_is_described_as()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var said = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action=replyas%E2%84%AAedback&subject={Agent}", UriKind.Relative), Ct));

        // Described as the reply, so drawn with the box the reply is written in.
        Assert.Contains("asked Parley something back instead of answering", said, StringComparison.Ordinal);
        Assert.Contains("Your reply:", said, StringComparison.Ordinal);
    }
}
