using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the Register page's last step sends a person to where the new agent appears.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 3 run 67.</b> The step said the new name appears on "this project's agents" and linked to
/// the Conversations page, so a person following it never saw the name and was told to suspect the session.
/// </remarks>
public sealed class RegisterLeadsToTheAgentsTests
{
    [Fact]
    public async Task The_confirming_step_links_to_the_agents_list()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri($"/Projects/Register?project={DeadBrokerConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains($"href=\"/?tab=agents&amp;project={DeadBrokerConsole.Project}\">this project's agents</a>", body, StringComparison.Ordinal);    }
}
