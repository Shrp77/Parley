using Parley.Cli.Top;
using Xunit;

namespace Parley.Cli.Tests.Top;

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
    [InlineData("catalogued, not yet registered")]
    [InlineData("not in this catalogue")]
    public void The_states_it_quotes_are_the_states_the_surface_draws(string phrase)
    {
        // Quoted in italics in the README as words a person will see on screen, which makes them a
        // promise rather than a description.
        Assert.Contains(phrase, Readme(), StringComparison.Ordinal);
        Assert.Contains(phrase, new[] { AgentsRegion.CataloguedOnly, AgentsRegion.NotCatalogued });
    }

    [Fact]
    public void The_width_it_names_is_the_width_the_layout_uses()
    {
        // "Below eighty columns the four stack." A number in prose and a number in code that drift
        // apart leave a reader who trusts the first unable to reproduce what they were told.
        Assert.Equal(80, Shell.Threshold);
        Assert.Contains("eighty columns", Readme(), StringComparison.Ordinal);
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
        Assert.Contains("parley top", Readme(), StringComparison.Ordinal);
        Assert.True(Readme().Length > 10_000);
    }
}
