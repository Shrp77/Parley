using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the document somebody reads before running this describes the thing they then run.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.10. Run 3.5 checked the feature's own contracts and run 3.9 the
/// requirement numbers. Neither looked at the README, which is the only one of the three a user
/// reads — and the one that ages fastest, because it is written once when a feature lands and then
/// left while the feature moves.
/// </para>
/// <para>
/// Only the checkable claims are checked, and deliberately so: the exact phrases it quotes back at
/// the reader, the column count it names, and the key it tells them to press. Prose about why the
/// surface is shaped this way is not something a test can hold to account, and pretending otherwise
/// would put a green check on a paragraph nobody verified.
/// </para>
/// </remarks>
public sealed class ReadmeTests
{
    private static string Readme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "README.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(directory!.FullName, "README.md"));
    }

    [Theory]
    [InlineData(Standing.NeverRun)]
    [InlineData(Standing.OnlyWhenOpened)]
    public void The_states_it_quotes_are_the_states_the_surface_draws(string phrase)
    {
        // Quoted in italics in the README as words a person will see on screen, which makes them a
        // promise rather than a description.
        //
        // 006 replaced the two phrases this used to name. They were `catalogued, not yet
        // registered` and `not in this catalogue`, and the owner read both on screen and could not
        // say what either meant -- fairly, because they named two files a person did not write.
        // The constants still exist in the region, so this had to be pointed at the wording that
        // is actually drawn rather than at whatever the surface still happens to declare.
        Assert.Contains(phrase, Readme(), StringComparison.Ordinal);
        Assert.Contains(
            phrase,
            new[]
            {
                Standing.Running, Standing.Idle, Standing.OnlyWhenOpened,
                Standing.NoMachine, Standing.NeverRun, Standing.NeverRunStuck,
            });
    }

    [Fact]
    public void It_does_not_still_quote_the_phrases_that_were_replaced()
    {
        // The half a rewording forgets. A README that gained the new phrases and kept the old ones
        // would promise a reader they will see words the surface no longer draws.
        var readme = Readme();

        Assert.DoesNotContain(AgentsRegion.CataloguedOnly, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentsRegion.NotCatalogued, readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_width_it_names_is_the_width_the_layout_uses()
    {
        // "Below <n> columns the four stack." A number in prose and a number in code that drift
        // apart leave a reader who trusts the first unable to reproduce what they were told.
        //
        // Spelled from the constant rather than written out a third time. The first draft asserted
        // the literal 80 in both places, which made the test a second copy of the number it was
        // meant to be checking — and made moving the threshold a three-file edit that fails the
        // build if any one of them is forgotten, which is the same drift wearing a green tick.
        var words = new Dictionary<int, string>
        {
            [80] = "eighty columns",
            [200] = "two hundred columns",
        };

        Assert.True(
            words.TryGetValue(Shell.Threshold, out var spelled),
            $"README has no wording for a threshold of {Shell.Threshold}; add one here and there.");

        Assert.Contains(spelled!, Readme(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_it_tells_people_to_press_is_the_key_that_is_bound()
    {
        Assert.Contains($"Press `{Shell.ActionsKey}`", Readme(), StringComparison.Ordinal);
    }

    [Fact]
    public void It_names_the_four_regions_the_surface_has()
    {
        var readme = Readme();

        foreach (var word in new[] { "projects", "agents", "conversations", "transcript" })
        {
            Assert.Contains(word, readme, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void It_says_a_destructive_act_takes_a_name_typed_back()
    {
        // The one property of this surface a person most needs to know before running it, and the
        // one the operations contract makes the door. If the README stopped saying it, somebody
        // would be surprised by a confirmation rather than reassured by one.
        var readme = Readme();

        Assert.Contains("typed back", readme, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        Assert.Contains("`parley` on its own opens it", Readme(), StringComparison.Ordinal);
        Assert.True(Readme().Length > 10_000);
    }
}
