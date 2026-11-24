using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Freezing everyone in a project where every agent is already frozen says that, and freezes nobody again.</summary>
/// <remarks>
/// Found by review, lens 3 run I on 19cd7a08: the sentence could be swapped for unfreezing's ("no agent is frozen") with
/// every test passing, which tells a person about to restart the broker the opposite of what is true.
/// </remarks>
[Integration]
public sealed partial class FreezingEveryoneWhenAllAreFrozenTests
{
    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    [Fact]
    public async Task Freezing_everyone_when_every_agent_is_frozen_says_they_already_are()
    {
        var ct = TestContext.Current.CancellationToken;

        using var console = new RoadmapConsole();

        foreach (var agent in new[] { "orders@test", "billing@test" })
        {
            console.Broker.Registry.Add(new Wire.RegistryRow
            {
                Name = agent, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
                FrozenUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture),
            });
        }

        using var client = console.CreateClient();

        var address = new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action=freezeall&subject={RoadmapConsole.Project}", UriKind.Relative);
        var described = await client.GetStringAsync(address, ct);

        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", Token().Match(described).Groups[1].Value)]);
        using var response = await client.PostAsync(address, form, ct);

        var after = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(ct));

        Assert.Empty(console.Broker.Frozen);
        Assert.Contains($"Nothing to freeze: every agent in '{RoadmapConsole.Project}' is already frozen", after, StringComparison.Ordinal);
        Assert.DoesNotContain("no agent in", after, StringComparison.Ordinal);
    }
}
