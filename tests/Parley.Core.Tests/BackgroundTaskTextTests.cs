using Xunit;

namespace Parley.Core.Tests;

/// <summary>What the words of a background task are held to before anybody sees them (feature 043).</summary>
/// <remarks>
/// <para>
/// A task's name and description come from the runner's output and from the agent itself, and are drawn in the person's
/// own view. So each is cut to the person's bound (a name 64 characters, a description 300) and has every character
/// that could break, hide or reorder a line made a space.
/// </para>
/// <para>
/// A link is matched against four fixed shapes and otherwise dropped: it is the one thing in a task the person may
/// follow, so nothing that hides where it goes is kept.
/// </para>
/// </remarks>
[Unit]
public sealed class BackgroundTaskTextTests
{
    [Fact]
    public void A_name_is_kept_as_given_when_it_is_plain_and_short()
    {
        var name = BackgroundTaskText.Name("Checking VAT on gift cards");

        Assert.Equal("Checking VAT on gift cards", name.Text);
        Assert.False(name.Cut);
    }

    [Fact]
    public void A_name_is_cut_to_sixty_four_characters_with_an_ellipsis()
    {
        var name = BackgroundTaskText.Name(new string('a', 200));

        Assert.True(name.Cut);
        Assert.Equal(BackgroundTaskText.LongestName, new System.Globalization.StringInfo(name.Text).LengthInTextElements);
        Assert.EndsWith("…", name.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_is_cut_to_three_hundred_characters()
    {
        var description = BackgroundTaskText.Description(new string('b', 1000));

        Assert.True(description.Cut);
        Assert.Equal(BackgroundTaskText.LongestDescription, new System.Globalization.StringInfo(description.Text).LengthInTextElements);
    }

    [Fact]
    public void Characters_counted_are_what_a_person_counts_not_bytes()
    {
        // Sixty-four emoji, each two UTF-16 units, fit; a cut never splits one.
        var name = BackgroundTaskText.Name(string.Concat(Enumerable.Repeat("😀", 64)));

        Assert.False(name.Cut);

        var longer = BackgroundTaskText.Name(string.Concat(Enumerable.Repeat("😀", 70)));

        Assert.True(longer.Cut);
        // A lone half of a pair reads back as the replacement character.
        Assert.DoesNotContain(System.Text.Rune.ReplacementChar, longer.Text.EnumerateRunes());
    }

    [Fact]
    public void One_character_built_of_many_marks_cannot_carry_more_than_the_bound_allows()
    {
        // A single text element can be any length; the cut holds the units too.
        var heavy = "a" + new string((char)0x0301, 5000);

        var name = BackgroundTaskText.Name(heavy);

        Assert.True(name.Text.Length <= BackgroundTaskText.LongestName * BackgroundTaskText.UnitsPerCharacter);
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x1B)]
    [InlineData(0x202E)]
    [InlineData(0x2028)]
    [InlineData(0x200B)]
    public void Anything_that_could_break_hide_or_reorder_a_line_is_a_space(int code)
    {
        var bad = (char)code;
        var name = BackgroundTaskText.Name("before" + bad + "after");

        Assert.DoesNotContain(bad, name.Text);
        Assert.Equal("before after", name.Text);
    }

    [Fact]
    public void An_empty_or_blank_name_is_none()
    {
        Assert.Equal(string.Empty, BackgroundTaskText.Name("   ").Text);
        Assert.Equal(string.Empty, BackgroundTaskText.Name("\n\t").Text);
    }

    [Theory]
    [InlineData("b8n705a3e", true)]
    [InlineData("af7da04eab7667f03", true)]
    [InlineData("task_1-a", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("dot.dot", false)]
    [InlineData("é", false)]
    public void A_task_id_is_letters_digits_hyphens_and_underscores(string id, bool valid)
    {
        Assert.Equal(valid, BackgroundTaskText.IsTaskId(id));
    }

    [Fact]
    public void A_task_id_has_at_most_sixty_four_characters()
    {
        Assert.True(BackgroundTaskText.IsTaskId(new string('a', 64)));
        Assert.False(BackgroundTaskText.IsTaskId(new string('a', 65)));
    }

    [Theory]
    [InlineData("exchange #57", TaskLinkKind.Exchange, "57")]
    [InlineData("#57", TaskLinkKind.Exchange, "57")]
    [InlineData("Exchange #9", TaskLinkKind.Exchange, "9")]
    [InlineData("conversation:UI-refresh-and-fixes", TaskLinkKind.Conversation, "UI-refresh-and-fixes")]
    [InlineData("file:vat-rules.md", TaskLinkKind.File, "vat-rules.md")]
    [InlineData("https://tickets.example.com/browse/SHOP-12", TaskLinkKind.Web, "https://tickets.example.com/browse/SHOP-12")]
    public void A_link_of_a_kind_Parley_shows_is_kept(string given, TaskLinkKind kind, string target)
    {
        var link = BackgroundTaskText.Link(given);

        Assert.NotNull(link);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(target, link.Target);
    }

    [Theory]
    [InlineData("http://tickets.example.com/SHOP-12")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>hi</b>")]
    [InlineData("https://exa" + "\u202E" + "mple.com/")]
    [InlineData("https:///no-host")]
    [InlineData("//example.com/relative")]
    [InlineData("/Projects/Act?action=purge")]
    [InlineData("exchange #0")]
    [InlineData("exchange #-3")]
    [InlineData("exchange #99999999999999999999")]
    [InlineData("conversation:has space")]
    [InlineData("file:../../secrets.md")]
    [InlineData("file:notes.exe")]
    [InlineData("https://example.com/\nsecond-line")]
    [InlineData("https://user:secret@example.com/")]
    public void Any_other_link_is_dropped(string given)
    {
        Assert.Null(BackgroundTaskText.Link(given));
    }

    [Fact]
    public void A_link_longer_than_five_hundred_characters_is_dropped()
    {
        Assert.Null(BackgroundTaskText.Link("https://example.com/" + new string('a', 500)));
    }

    [Fact]
    public void A_kept_link_is_written_back_in_one_spelling()
    {
        Assert.Equal("exchange #57", BackgroundTaskText.Link("#57")!.Written);
        Assert.Equal("conversation:returns", BackgroundTaskText.Link("conversation:returns")!.Written);
        Assert.Equal("file:a.md", BackgroundTaskText.Link("file:a.md")!.Written);
    }

    [Fact]
    public void A_web_link_says_its_host()
    {
        Assert.Equal("tickets.example.com", BackgroundTaskText.Link("https://tickets.example.com/x")!.Host);
    }
}
