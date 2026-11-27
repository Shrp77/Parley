using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The top bar names the conversation a page is for, beside its project, and names none on a page that is for none.
/// </summary>
/// <remarks>
/// <b>Feature 042.</b> The name stays in sight however far the transcript is scrolled. Found by review: the guard that draws
/// it could be made never to draw it, and every test passed.
/// </remarks>
[Integration]
public sealed class TheTopBarNamesTheConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_conversations_page_names_it_beside_its_project()
    {
        var where = await WhereAsync($"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared");

        Assert.Contains("<a class=\"crumb\" href=\"/Projects/Agents?project=alpha\" title=\"This project\">alpha</a>", where, StringComparison.Ordinal);
        Assert.Contains("<span class=\"crumb-here\" title=\"shared\">shared</span>", where, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_for_no_conversation_names_only_its_project()
    {
        var where = await WhereAsync($"/Projects/Agents?project={TranscriptConsole.Project}");

        Assert.Contains("<a class=\"crumb\" href=\"/Projects/Agents?project=alpha\" title=\"This project\">alpha</a>", where, StringComparison.Ordinal);
        Assert.DoesNotContain("crumb-here", where, StringComparison.Ordinal);
    }

    /// <summary>The top bar's place in the deployment, as drawn.</summary>
    private static async Task<string> WhereAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
        var where = Regex.Match(page, "<header>\\s*<span class=\"where\">(?<where>.*?)<span class=\"stamps\">", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        Assert.True(where.Success, "the top bar does not say where the page is");
        return where.Groups["where"].Value;
    }
}
