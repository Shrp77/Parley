using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a conversation's name is made acceptable as it is typed rather than refused afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>A typed name is corrected, not refused.</b> Telling every agent in a project something, naming the
/// conversation <c>tell everybody test</c>, writing the message and sending it must not produce a sentence
/// about which characters a name may hold, with the form closed and what was written gone.
/// </para>
/// <para>
/// The rule is one rule and is checked here rather than on either surface: the terminal's composer
/// and the console's <i>Start a conversation called</i> field both apply it, and
/// <c>SeatVoice</c> refuses with it at the send. Two implementations would be two opinions about
/// which names are usable, and the one that disagreed would be the one letting something through.
/// </para>
/// </remarks>
[Unit]
public sealed class ConversationNameTests
{
    [Theory]
    [InlineData("tell everybody test", "tell-everybody-test")]
    [InlineData("Release 1.2 / notes", "Release-1.2-notes")]
    [InlineData("two  spaces", "two-spaces")]
    [InlineData("who owns the cache?", "who-owns-the-cache")]
    [InlineData("already-fine.1", "already-fine.1")]
    [InlineData("tabs\tand\nbreaks", "tabs-and-breaks")]
    public void A_name_is_corrected_to_something_the_rule_accepts(string typed, string expected)
    {
        var corrected = ConversationName.Correct(typed);

        Assert.Equal(expected, corrected);

        // And what comes out is genuinely acceptable rather than merely different. A correction
        // that disagreed with the rule would be a field rewriting text it then refuses.
        Assert.Null(ConversationName.Problem(corrected));
    }

    [Fact]
    public void Spaces_become_hyphens_rather_than_being_dropped()
    {
        // The one place this departs from the project-name correction beside it, and the reason is
        // legibility: a name reaches a filename and gets read back by a person.
        Assert.Equal("tell-everybody-test", ConversationName.Correct("tell everybody test"));
        Assert.NotEqual("telleverybodytest", ConversationName.Correct("tell everybody test"));
    }

    [Fact]
    public void Correcting_twice_changes_nothing_the_second_time()
    {
        // What a field depends on: correcting sets the field's text, which raises the changed
        // event again. A correction that did not settle would recurse until the stack ran out.
        foreach (var typed in new[] { "tell everybody test", "Ärger im Café", "...", "a b  c" })
        {
            var once = ConversationName.Correct(typed);

            Assert.Equal(once, ConversationName.Correct(once));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a..b")]
    public void It_corrects_and_never_completes(string typed)
    {
        // Left for the message to refuse. Collapsing somebody's dots or inventing a name for an
        // empty field is deciding what they meant, and this names a file that gets written.
        Assert.Equal(typed, ConversationName.Correct(typed));
        Assert.NotNull(ConversationName.Problem(typed));
    }

    [Fact]
    public void A_name_past_the_cap_is_refused_rather_than_truncated()
    {
        // A length is a rule somebody can act on while their words are still in front of them,
        // and cutting text nobody asked to cut is the defect this whole change is about.
        var long_ = new string('a', ConversationName.Longest + 1);

        Assert.Equal(long_, ConversationName.Correct(long_));
        Assert.NotNull(ConversationName.Problem(long_));
        Assert.Null(ConversationName.Problem(new string('a', ConversationName.Longest)));
    }

    [Fact]
    public void The_rule_the_console_enforces_is_the_rule_this_applies()
    {
        // `Pattern` is what a browser with no scripts holds a field to and `Allowed` is what the
        // field applies as it is typed. Neither is derived from `Correct`, so they are checked
        // against it here -- a pattern that admitted what the rule refuses would make the console
        // the surface that lets something through.
        var pattern = new System.Text.RegularExpressions.Regex(
            "^(?:" + ConversationName.Pattern + ")$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var allowed = new System.Text.RegularExpressions.Regex(
            "^" + ConversationName.Allowed + "$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        foreach (var name in new[] { "tell-everybody-test", "a", "a.b_c-1", new string('z', 64) })
        {
            Assert.True(pattern.IsMatch(name), $"the pattern refuses '{name}', which the rule allows");
            Assert.Null(ConversationName.Problem(name));
        }

        foreach (var name in new[] { "tell everybody test", "a..b", "", new string('z', 65), "a/b" })
        {
            Assert.False(pattern.IsMatch(name), $"the pattern admits '{name}', which the rule refuses");
            Assert.NotNull(ConversationName.Problem(name));
        }

        // And the character class keeps exactly what the correction keeps.
        for (var c = ' '; c < (char)127; c++)
        {
            var kept = ConversationName.Correct(c.ToString()) == c.ToString();

            Assert.Equal(kept, allowed.IsMatch(c.ToString()));
        }
    }

    [Fact]
    public void The_refusal_says_the_rule_and_what_it_is_for()
    {
        // §7.3's habit applied to a person: a refusal returns work rather than a retry
        // instruction. Somebody reading this has to invent a name that satisfies a rule they
        // cannot see, so the sentence carries it -- and says why, because the reason is the part
        // that makes an odd-looking rule memorable.
        var said = ConversationName.Problem("tell everybody test");

        Assert.NotNull(said);
        Assert.Contains("tell everybody test", said, StringComparison.Ordinal);
        Assert.Contains("64", said, StringComparison.Ordinal);
        Assert.Contains("filename", said, StringComparison.Ordinal);
    }
}
