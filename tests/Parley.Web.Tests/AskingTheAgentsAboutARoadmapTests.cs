using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Asking every agent how a roadmap stands (SPEC.md §5.7).</summary>
/// <remarks>
/// The person's own Request, described before it is sent, sent only on a POST, to every active agent but the person and
/// Parley, in one conversation, and asking the agents not to propose a roadmap in answer.
/// </remarks>
[Integration]
public sealed class AskingTheAgentsAboutARoadmapTests
{
    private static readonly Uri CheckPage = new($"/Roadmaps/Check?project={RoadmapConsole.Project}&id=1", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoadmapConsole Confirmed()
    {
        var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", IntendedAgent = "billing@test", ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        });
        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 2, Number = 2, Position = 2, Text = "Document the export", ConversationName = "rm1-2",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        });
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 2 });
        console.Broker.Seats = true;

        foreach (var (name, adapter) in new[]
        {
            ("billing@test", "claude-code"), ("orders@test", "claude-code"), ("retired@test", "claude-code"),
            ("human-user@" + RoadmapConsole.Project, "parley-console"), ("parley-facilitator@" + RoadmapConsole.Project, "parley-facilitator"),
        })
        {
            console.Broker.Registry.Add(new Wire.RegistryRow
            {
                Name = name,
                Adapter = adapter,
                Tier = Wire.AgentTier.Full,
                Status = name.StartsWith("retired", StringComparison.Ordinal) ? Wire.AgentStatus.Retired : Wire.AgentStatus.Active,
            });
        }

        return console;
    }

    [Fact]
    public async Task The_roadmap_page_offers_it_and_following_the_link_sends_nothing()
    {
        using var console = Confirmed();
        using var client = console.CreateClient();

        var roadmap = await client.GetStringAsync(new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1", UriKind.Relative), Ct);

        Assert.Contains("/Roadmaps/Check?project=" + RoadmapConsole.Project + "&amp;id=1", roadmap, StringComparison.Ordinal);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync(CheckPage, Ct));

        Assert.Empty(console.Broker.Requested);
        Assert.Contains("every agent in the project", page, StringComparison.Ordinal);
        Assert.Contains("billing@test, orders@test", page, StringComparison.Ordinal);
        Assert.DoesNotContain("human-user@", page.Split("Going to:")[1].Split('.')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("retired@test", page, StringComparison.Ordinal);
        Assert.Contains("1. Add CSV export - for billing@test - not started", page, StringComparison.Ordinal);
        Assert.Contains("2. Document the export", page, StringComparison.Ordinal);
        Assert.Contains("Do not call propose_roadmap", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posting_sends_one_Request_to_each_active_agent_in_one_conversation_and_nobody_else()
    {
        using var console = Confirmed();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(CheckPage, Ct);
        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(token);
        Assert.Empty(console.Broker.Requested);

        using var posted = await client.PostAsync(
            CheckPage, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal(["billing@test", "orders@test"], console.Broker.Requested.Select(r => r.To).Order(StringComparer.Ordinal));
        Assert.Single(console.Broker.Requested.Select(r => r.ConversationId).Distinct(StringComparer.Ordinal));
        Assert.StartsWith("roadmap-1-check-", console.Broker.Requested[0].ConversationId, StringComparison.Ordinal);
        Assert.All(console.Broker.Requested, r => Assert.Contains("Add CSV export", r.PayloadJson, StringComparison.Ordinal));
        Assert.All(console.Broker.Requested, r => Assert.Equal("human-user@" + RoadmapConsole.Project, r.From));
    }
}
