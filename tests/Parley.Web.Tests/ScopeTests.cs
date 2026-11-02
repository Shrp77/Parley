using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That nothing here is ever unscoped, and that a filter which could not be applied says so.
/// </summary>
/// <remarks>
/// <para>
/// The project is the context for everything below it. SPEC.md §4.1 gives each its own store and
/// endpoint, so two environments may each hold an <c>orders</c> — and a page that showed both
/// would present colliding names with nothing saying which was which. There is no search that is
/// not the search of one project, and the enforcement is that every route below the list carries
/// the project and redirects without it.
/// </para>
/// <para>
/// The second half is the quieter failure. A filter that is dropped because it could not be
/// parsed returns everything in the project, which is not an empty result somebody would question
/// but a full one they would read as the answer to what they asked.
/// </para>
/// </remarks>
[Integration]
public sealed class ScopeTests
{
    [Theory]
    [InlineData("/Projects/Agents")]
    [InlineData("/Projects/Register")]
    [InlineData("/Projects/Health")]
    public async Task Every_page_below_the_list_refuses_to_render_without_a_project(string page)
    {
        // Not "defaults to the first project", and not "shows everything". Either would be a page
        // whose contents belong to an environment the reader did not choose.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(page, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_conversation_cannot_be_opened_without_naming_the_project_it_is_in()
    {
        // A conversation identifier is opaque and chosen by an agent (§5.0), so the same one may
        // exist in two projects. Opening one without saying which is a coin toss the reader would
        // never know had been made.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/Conversations/Detail?id=shared", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task A_filter_that_is_not_a_name_is_reported_rather_than_dropped()
    {
        // Silently widening is the failure: the operator typed something to narrow the list, got
        // the whole project back, and nothing on the page says the filter was ignored.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Projects/Agents?project={DeadBrokerConsole.Project}&participant=not a name",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("was not used to narrow this list", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_that_is_a_name_is_not_complained_about()
    {
        // The other side of it. A warning on every search is a warning nobody reads, including on
        // the search where it matters.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Projects/Agents?project={DeadBrokerConsole.Project}"
                + $"&participant=orders@{DeadBrokerConsole.Project}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("was not used to narrow this list", body, StringComparison.Ordinal);
    }
}
