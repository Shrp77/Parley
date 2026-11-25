using Microsoft.AspNetCore.Http;
using Parley.Web.Pages.Shared;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>A filter chip's link is the same list with that one filter off.</summary>
/// <remarks>
/// Feature 042. A chip that dropped another part of the address (the tab, the project, the Inbox's view or how far back it
/// reads) would take the person somewhere else than the list they were reading; one that kept the reload mark would make
/// their press read as a reload.
/// </remarks>
[Unit]
public sealed class FilterChipsTests
{
    [Fact]
    public void Taking_one_filter_off_keeps_every_other_part_of_the_address()
    {
        var request = Request("/", "?tab=agents&project=alpha&q=orders&state=working");

        Assert.Equal("/?tab=agents&project=alpha&state=working", FilterChips.Without(request, "q"));
        Assert.Equal("/?tab=agents&project=alpha&q=orders", FilterChips.Without(request, "state"));
    }

    [Fact]
    public void The_name_is_matched_as_model_binding_reads_it_and_the_reload_mark_is_dropped()
    {
        // "reloaded" is the console's mark for its own timed reload (PersonSeats.Reloaded).
        var request = Request("/Inbox", "?view=closed&Q=x&reloaded=1&older=2");

        Assert.Equal("/Inbox?view=closed&older=2", FilterChips.Without(request, "q"));
    }

    [Fact]
    public void A_value_is_encoded_once()
    {
        var request = Request("/", "?q=a%26b%20c&status=up");

        Assert.Equal("/?q=a%26b%20c", FilterChips.Without(request, "status"));
    }

    private static HttpRequest Request(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        return context.Request;
    }
}
