using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That three views drill in and back out, and that back keeps the place rather than the level.
/// </summary>
/// <remarks>
/// Derived once because both surfaces need the same answer to "what does back mean". 004's console
/// and terminal implemented seating separately and disagreed about who was in a conversation twice
/// in one week; navigation is the same kind of thing, and the second implementation would have got
/// the empty cases subtly different.
/// </remarks>
public sealed class NavigationTests
{
    private static ProjectName Alpha => ProjectName.Create("alpha");

    [Fact]
    public void The_top_is_the_projects_view_and_has_nowhere_to_go_back_to()
    {
        Assert.Equal(Viewing.Projects, ViewPlace.Top.Viewing);
        Assert.False(ViewPlace.Top.CanGoBack);
        Assert.Equal(ViewPlace.Top, ViewPlace.Top.Back());
    }

    [Fact]
    public void A_default_place_is_the_top()
    {
        // Zero is Projects on purpose: there is always somewhere to go back to from anywhere else,
        // and nowhere from here, so the harmless value is the default.
        Assert.Equal(Viewing.Projects, default(Viewing));
    }

    [Fact]
    public void Opening_a_project_and_coming_back_returns_to_the_projects_view()
    {
        var inside = ViewPlace.Into(Alpha);

        Assert.Equal(Viewing.Project, inside.Viewing);
        Assert.Equal(Alpha, inside.Project);
        Assert.Equal(ViewPlace.Top, inside.Back());
    }

    [Fact]
    public void Coming_back_from_a_conversation_lands_on_the_project_it_was_in()
    {
        // The property that makes reading three conversations in turn cost three keystrokes rather
        // than three descents from the top.
        var inside = ViewPlace.Into(Alpha).Into("some-discussion");

        Assert.Equal(Viewing.Conversation, inside.Viewing);
        Assert.Equal("some-discussion", inside.Conversation);

        var back = inside.Back();

        Assert.Equal(Viewing.Project, back.Viewing);
        Assert.Equal(Alpha, back.Project);
        Assert.Null(back.Conversation);
    }

    [Fact]
    public void Back_twice_from_a_conversation_reaches_the_top_and_stops()
    {
        var place = ViewPlace.Into(Alpha).Into("some-discussion").Back().Back();

        Assert.Equal(ViewPlace.Top, place);
        Assert.Equal(ViewPlace.Top, place.Back());
    }

    [Fact]
    public void A_conversation_cannot_be_opened_without_a_project()
    {
        // Every broker call is per project, so a place that allowed this would fail later, at a
        // call site with much less to say about why.
        Assert.Throws<InvalidOperationException>(() => ViewPlace.Top.Into("some-discussion"));
    }

    [Fact]
    public void Each_view_says_what_it_is_about()
    {
        Assert.Equal("Projects", ViewPlace.Top.Title);
        Assert.Equal("alpha", ViewPlace.Into(Alpha).Title);
        Assert.Equal("some-discussion", ViewPlace.Into(Alpha).Into("some-discussion").Title);
    }

    [Theory]
    [InlineData(Viewing.Projects, "/")]
    [InlineData(Viewing.Project, "/Projects/Agents?project=alpha")]
    public void The_browser_key_opens_the_thing_on_screen(Viewing viewing, string expected)
    {
        var place = viewing == Viewing.Projects ? ViewPlace.Top : ViewPlace.Into(Alpha);

        Assert.Equal("http://127.0.0.1:5280" + expected, Deep.For("http://127.0.0.1:5280", place));
    }

    [Fact]
    public void The_browser_key_on_a_conversation_names_the_conversation()
    {
        var place = ViewPlace.Into(Alpha).Into("some-discussion");

        Assert.Equal(
            "http://127.0.0.1:5280/Conversations/Detail?project=alpha&id=some-discussion",
            Deep.For("http://127.0.0.1:5280", place));
    }

    [Fact]
    public void A_trailing_slash_on_the_host_does_not_double_up()
    {
        Assert.Equal(
            "http://127.0.0.1:5280/Projects/Agents?project=alpha",
            Deep.For("http://127.0.0.1:5280/", ViewPlace.Into(Alpha)));
    }
}
