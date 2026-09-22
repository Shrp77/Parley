using Parley.Client.Seating;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The narrowing list behind both sigils.
/// </summary>
/// <remarks>
/// <para>
/// The owner's interaction model, and their reason for it: "this way accidental typos in commands are less
/// likely". Since an unrecognised identifier is sent as text, a command typed blind and wrong costs somebody an
/// ask they must answer — so the remedy is to make typing one blind the unusual path.
/// </para>
/// <para>
/// Two properties here are load-bearing and both are probed: the list is derived from the text rather than
/// remembered, which is the only way deleting back into a match can bring it back; and prefix fills the list
/// while an exact identifier decides the send, which are two different comparisons.
/// </para>
/// </remarks>
public sealed class CompletionTests
{
    private static readonly string[] Ids = ["add", "archive", "close"];
    private static readonly string[] Agents = ["billing@test", "orders@test", "inventory@test"];

    private static Completion Read(string text, int selected = 0) =>
        Completion.Read(text, Ids, Agents, selected);

    [Fact]
    public void A_slash_opens_every_command()
    {
        var list = Read("/");

        Assert.Equal(Sigil.Command, list.Which);
        Assert.True(list.Open);
        Assert.Equal(Ids.Length, list.Matches.Count);
    }

    [Fact]
    public void And_it_narrows_as_more_is_typed()
    {
        Assert.Equal(["add", "archive"], Read("/a").Matches);
        Assert.Equal(["add"], Read("/ad").Matches);
    }

    [Fact]
    public void Nothing_matching_is_a_closed_list_rather_than_an_empty_one()
    {
        // "None waiting" and "could not ask" are different facts everywhere else in this codebase; here the
        // distinction is that a closed list means the draft is text.
        var list = Read("/zzz");

        Assert.False(list.Open);
        Assert.Empty(list.Matches);
    }

    [Fact]
    public void Deleting_back_into_a_match_opens_it_again()
    {
        // The property that forbids a remembered "dismissed" flag: there is only the text and what it matches.
        Assert.False(Read("/added").Open);
        Assert.True(Read("/add").Open);
        Assert.True(Read("/a").Open);
    }

    [Fact]
    public void A_finished_identifier_is_not_being_completed_any_more()
    {
        // What follows a space is a parameter or prose, not more of the identifier.
        Assert.Equal(Sigil.None, Read("/add ").Which);
        Assert.Equal(Sigil.None, Read("/add @bil").Which is Sigil.Agent ? Sigil.None : Read("/add ").Which);
    }

    [Fact]
    public void A_second_slash_is_not_a_command_being_completed()
    {
        // The path rule, at the list as well as at the send: `/etc/hosts` must not open a list either.
        Assert.False(Read("/etc/hosts").Open);
        Assert.Equal(Sigil.None, Read("/etc/hosts").Which);
    }

    [Fact]
    public void An_at_sign_opens_the_agents_wherever_it_was_typed()
    {
        // Addressing is something a person does part way through a sentence, unlike a command.
        var list = Read("and refunds? @bil");

        Assert.Equal(Sigil.Agent, list.Which);
        Assert.Equal(["billing@test"], list.Matches);
    }

    [Fact]
    public void The_last_at_sign_is_the_one_being_completed()
    {
        var list = Read("@billing and @ord");

        Assert.Equal(["orders@test"], list.Matches);
    }

    [Fact]
    public void A_command_only_completes_at_the_start_of_the_line()
    {
        // Mid-sentence a slash is a slash. A person asking about a path is not reaching for a command.
        Assert.Equal(Sigil.None, Read("what about /add").Which);
    }

    [Fact]
    public void Tab_completes_the_highlighted_match()
    {
        Assert.Equal("add", Read("/a").Completes());
        Assert.Equal("archive", Read("/a").Move(1).Completes());
    }

    [Fact]
    public void A_closed_list_completes_nothing()
    {
        Assert.Null(Read("/zzz").Completes());
    }

    [Fact]
    public void Moving_wraps_at_each_end()
    {
        // 0.20.3 records what a list that cannot be left in one direction costs: every group was a room with no
        // door out of the top.
        var list = Read("/");

        Assert.Equal(Ids[^1], list.Move(-1).Completes());
        Assert.Equal(Ids[0], list.Move(Ids.Length).Completes());
    }

    [Fact]
    public void A_highlight_past_the_end_of_a_narrowed_list_is_brought_back_into_it()
    {
        // Typing narrows under the highlight. Left alone it would point past the end and Tab would complete
        // nothing, which reads as a broken key.
        var list = Read("/ad", selected: 2);

        Assert.Equal("add", list.Completes());
    }

    [Fact]
    public void Completion_is_case_insensitive_because_a_person_typing_fast_is_not()
    {
        Assert.Equal(["add"], Read("/AD").Matches);
        Assert.Equal(["billing@test"], Read("@BIL").Matches);
    }

    [Fact]
    public void An_empty_line_is_completing_nothing()
    {
        Assert.Equal(Sigil.None, Read(string.Empty).Which);
        Assert.Equal(Sigil.None, Completion.Read(null, Ids, Agents).Which);
    }
}
