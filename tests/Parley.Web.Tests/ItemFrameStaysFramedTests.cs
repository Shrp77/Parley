using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>What a browser asks for from inside a roadmap item's frame: a send there stays in the frame.</summary>
[Integration]
public sealed class ItemFrameStaysFramedTests
{
    private static readonly Uri Framed =
        new($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1&With=billing%40test&embed=1", UriKind.Relative);

    private static RoadmapConsole Confirmed(bool started)
    {
        var console = new RoadmapConsole();
        var item = new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", IntendedAgent = "billing@test", ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = started },
        };
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.Add(item);
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 1 });
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });

        return console;
    }

    private static HttpRequestMessage FromTheFrame(HttpMethod method, Uri path)
    {
        var request = new HttpRequestMessage(method, path);

        // What a browser sends for a navigation of a nested browsing context: a link followed or a form submitted in it.
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        return request;
    }

    [Fact]
    public async Task A_send_from_inside_the_frame_returns_a_page_the_frame_may_show_without_the_consoles_chrome()
    {
        using var console = Confirmed(started: false);
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;

        using var get = FromTheFrame(HttpMethod.Get, Framed);
        using var first = await client.SendAsync(get, token);
        var html = await first.Content.ReadAsStringAsync(token);
        var antiforgery = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(antiforgery);

        using var post = FromTheFrame(HttpMethod.Post, Framed);
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgery, ["Says"] = "Add CSV export", ["Targets"] = "billing@test",
        });
        using var sent = await client.SendAsync(post, token);
        var after = await sent.Content.ReadAsStringAsync(token);

        Assert.Contains("frame-ancestors 'self'", sent.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("SAMEORIGIN", sent.Headers.GetValues("X-Frame-Options").Single());
        Assert.Matches("<body class=\"[^\"]* embed", after);
    }
}
