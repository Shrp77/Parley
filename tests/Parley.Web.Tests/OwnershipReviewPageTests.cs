using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The web UI's ownership review: reachable from the project's page, and honest when the broker cannot be read
/// (SPEC.md §10.2, FR-016).
/// </summary>
/// <remarks>
/// <b>Run with no broker listening, which is the case worth asserting.</b> A review that rendered an empty list when it
/// could not ask would read as "no agent has a line" — the one reading that sends a person to ask every agent again,
/// spending a turn on each for nothing. Every decision on the page is a link to <c>Act</c>, which
/// <c>EveryActionHasADoorTests</c> holds to describing itself before it acts.
/// </remarks>
public sealed class OwnershipReviewPageTests
{
    [Fact]
    public async Task The_review_is_linked_from_the_projects_settings()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Settings?project={DeadBrokerConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains($"/Projects/Ownership?project={DeadBrokerConsole.Project}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_read_is_said_rather_than_shown_as_nobody_having_a_line()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri($"/Projects/Ownership?project={DeadBrokerConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("role=\"alert\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no agent in this project yet", body, StringComparison.Ordinal);
    }
}
