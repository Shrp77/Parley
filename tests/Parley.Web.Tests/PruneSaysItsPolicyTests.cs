using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console says, before a prune, what the age cap reaches, and after it, under which policy it ran (SPEC.md §9.4).
/// </summary>
/// <remarks>
/// The page once promised that nothing not yet collected is removed, which the age cap contradicts, and its result named
/// only counts, so a deletion under numbers nobody realised were in force went unseen.
/// </remarks>
public sealed class PruneSaysItsPolicyTests
{
    private static string PrunePath => $"/Projects/Act?project={TranscriptConsole.Project}&action=prune&subject={TranscriptConsole.Project}";

    [Fact]
    public async Task The_confirmation_says_the_age_cap_reaches_uncollected_mail()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(PrunePath, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("until they pass the age cap", page, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing not yet collected", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_result_names_the_policy_it_applied()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        using var form = await client.GetAsync(new Uri(PrunePath, UriKind.Relative), token);
        var html = await form.Content.ReadAsStringAsync(token);
        var verification = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        Assert.NotEmpty(verification);

        using var request = new HttpRequestMessage(HttpMethod.Post, PrunePath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = verification }),
        };

        if (form.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        using var response = await client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        Assert.Contains("Removed 2 message(s).", body, StringComparison.Ordinal);
        Assert.Contains("Policy applied: a conversation quiet for 3 days is pruned, and nothing but open work is kept past 30 days.", body, StringComparison.Ordinal);
    }
}
