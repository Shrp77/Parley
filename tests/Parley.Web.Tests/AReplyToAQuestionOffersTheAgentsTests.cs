using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The reply box on the Act page offers the project's agents when @ is typed, as every message box does.</summary>
/// <remarks>
/// Found by review, lens 3 run I on 19cd7a08: the source check holds that the box passes <c>Mentions:</c>, and only the
/// Inbox's boxes were held to what it holds, so the Act page could pass an empty list and offer nobody with every test passing.
/// </remarks>
[Integration]
public sealed class AReplyToAQuestionOffersTheAgentsTests
{
    [Fact]
    public async Task The_reply_box_offers_the_projects_agents_and_not_the_person_or_parley()
    {
        using var console = new RoadmapConsole();

        console.Broker.Seats = true;

        foreach (var (name, adapter) in new[]
        {
            ("human-user@" + RoadmapConsole.Project, "parley-console"),
            ("parley-facilitator@" + RoadmapConsole.Project, "parley-facilitator"),
            ("orders@test", "claude-code"),
            ("billing@test", "claude-code"),
        })
        {
            console.Broker.Registry.Add(new Wire.RegistryRow
            {
                Name = name, Adapter = adapter, Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            });
        }

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action=replyaskedback&subject=orders@test", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var box = Regex.Match(page, "<textarea data-composer id=\"text\"[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(2)).Value;

        Assert.NotEmpty(box);
        Assert.Contains("data-mentions=\"billing@test orders@test\"", box, StringComparison.Ordinal);
    }
}
