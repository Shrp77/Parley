using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That acting on several conversations from the list keeps the properties the single acts have.
/// </summary>
/// <remarks>
/// Read from the source, as <c>ListingStateTests</c> does, because driving a post needs an antiforgery token and a live
/// broker, and what is held is a property of the lines: what the redirect carries back, what a bulk act asks the broker
/// rather than trusts the page for, and that ending names what it ends and takes a word before it does anything.
/// </remarks>
[Unit]
public sealed class BulkConversationActsTests
{
    private static string Read(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine([directory!.FullName, "src", "Parley.Web", "Pages", .. path]));
    }

    private static string BulkHandler()
    {
        var model = Read("Projects", "Agents.cshtml.cs");

        return model[model.IndexOf("OnPostBulkAsync", StringComparison.Ordinal)..];
    }

    [Fact]
    public void A_bulk_act_carries_the_listing_options_back_and_says_what_it_did()
    {
        var handler = BulkHandler();

        Assert.Contains("showArchived", handler, StringComparison.Ordinal);
        Assert.Contains("sort", handler, StringComparison.Ordinal);
        Assert.Contains("filed = done", handler, StringComparison.Ordinal);
        Assert.Contains("failed", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Archiving_all_closed_asks_the_broker_and_never_takes_an_open_conversation()
    {
        var handler = BulkHandler();

        Assert.Contains("c.Closed && !c.Archived && !c.HasOpenExchange", handler, StringComparison.Ordinal);
        Assert.Contains("broker.ConversationsAsync(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bulk_act_is_bounded_and_refuses_an_operation_it_does_not_know()
    {
        var handler = BulkHandler();

        Assert.Contains("Take(MostAtOnce)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceConclude", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Ending_lists_what_it_ends_and_does_nothing_until_the_word_is_typed()
    {
        var model = Read("Projects", "EndMany.cshtml.cs");

        var post = model[model.IndexOf("OnPostAsync", StringComparison.Ordinal)..];
        var word = post.IndexOf("Word", StringComparison.Ordinal);
        var act = post.IndexOf("ForceConcludeAsync", StringComparison.Ordinal);

        Assert.True(word > 0 && act > word, "the word is checked before anything is ended");
        Assert.Contains("Typed is null", post, StringComparison.Ordinal);
        Assert.Contains("return Page()", post, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_offers_ticks_that_belong_to_the_bulk_form_and_ending_goes_to_its_own_page()
    {
        var markup = Read("Projects", "Agents.cshtml");

        Assert.Contains("form=\"bulk\" name=\"ids\"", markup, StringComparison.Ordinal);
        Assert.Contains("formaction=\"/Projects/EndMany\"", markup, StringComparison.Ordinal);
        Assert.Contains("value=\"archiveclosed\"", markup, StringComparison.Ordinal);
        Assert.Contains("name=\"Typed\"", Read("Projects", "EndMany.cshtml"), StringComparison.Ordinal);
    }
}
