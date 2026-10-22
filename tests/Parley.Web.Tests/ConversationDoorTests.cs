using System.Net;
using System.Reflection;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console's two new endpoints cannot be reached by following a link, or by a form
/// somebody else wrote.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.4. 006 added two things to the console that change a
/// deployment: archiving a conversation and destroying one. `ActingTests` holds the 004 and 005
/// actions to the rule that a GET describes and only a POST does; these are the new ones and were
/// written after that test rather than beside it.
/// </para>
/// <para>
/// The rule matters because a link is followed by things that are not people: a browser
/// prefetching, an editor previewing a URL, whatever crawls a page somebody left open. A destroy on
/// a GET is a destroy anything can trigger by looking at it.
/// </para>
/// <para>
/// <b>Three of these were written wrong first and the code was right.</b> Two assumed a reachable
/// broker in a fixture that has none, and one asserted a status code where the property is about
/// which handlers exist. Kept in this shape because the fourth — the antiforgery one — is the
/// assertion those mistakes led to, and it is the one worth having.
/// </para>
/// </remarks>
public sealed class ConversationDoorTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    [Fact]
    public void No_handler_archives_or_destroys_on_a_get()
    {
        // The property, rather than a status code that depends on how the framework answers a
        // handler it will not run. What may not exist is a GET handler that changes anything.
        var pages = typeof(Program).Assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Model", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(pages);

        foreach (var page in pages)
        {
            foreach (var method in page.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!method.Name.StartsWith("OnGet", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.DoesNotContain("Archive", method.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Unarchive", method.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Destroy", method.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_conversation_in_the_list_offers_archiving_and_destroying()
    {
        // The combined review gate found both performed by this console and reached from no page — since 006, when they
        // were added. A handler nothing posts to and a page nothing links to are the 020 walk's finding again. Read from
        // the page's source, as `ListingStateTests` reads the redirect: what is checked is that the door is there at all.
        var page = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Web", "Pages", "Projects", "Agents.cshtml"));

        Assert.Contains("asp-page-handler=\"Archive\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/Conversations/Destroy?", page, StringComparison.Ordinal);

        // And archiving does not lose the listing's options on its redirect.
        Assert.Contains("name=\"showArchived\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"sort\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Archiving_exists_only_as_a_post_handler()
    {
        var agents = typeof(Program).Assembly.GetTypes()
            .Single(type => type.FullName == "Parley.Web.Pages.Projects.AgentsModel");

        Assert.NotNull(agents.GetMethod("OnPostArchiveAsync"));
        Assert.Null(agents.GetMethod("OnGetArchiveAsync"));
    }

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused()
    {
        // **The assertion the mistakes above led to, and the one that matters.** Destroying takes a
        // typed name, which stops a person doing it by accident; it does not stop a form on
        // somebody else's page submitting to this one with the name already filled in. The
        // identifier is not a secret -- it is in every export and every listing -- so the typed name
        // is no defence at all against a request the operator did not compose.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("project", DeadBrokerConsole.Project),
            new KeyValuePair<string, string>("id", "a-discussion"),
            new KeyValuePair<string, string>("typed", "a-discussion"),
        ]);

        using var response = await client.PostAsync(
            new Uri("/Conversations/Destroy", UriKind.Relative),
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_page_describes_the_broker_it_cannot_reach_rather_than_offering_a_form()
    {
        // A confirmation for a conversation the page could not read would ask somebody to type a
        // name back at a description it never showed them.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(
                $"/Conversations/Destroy?project={DeadBrokerConsole.Project}&id=a-discussion",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Type the conversation's identifier", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destroy_aimed_at_a_project_this_machine_does_not_have_goes_to_the_list()
    {
        // The same answer every project-scoped route gives: the wrong address rather than an error
        // page, and nothing attempted along the way.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/Conversations/Destroy?project=nothing-here&id=a", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
