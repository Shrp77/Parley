using System.Reflection;
using Parley.Cli.Top;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That rewriting the terminal did not widen what the tool exposes.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.1. A rewrite is the moment an API surface grows by accident:
/// sixteen files were replaced with fifteen, and every one of them needed something from its
/// neighbours. The check is that <c>Parley.Cli</c> still exposes the commands and nothing else —
/// a public view or a public dialog would be a promise nobody meant to make, and it would be
/// binding the next time somebody wanted to change the layout.
/// </para>
/// <para>
/// Asserted by reflecting over the real assembly rather than by reading the files, because
/// <c>internal</c> is a keyword somebody can forget and a partial rewrite is exactly when.
/// </para>
/// </remarks>
public sealed class SurfaceShapeTests
{
    private static IEnumerable<Type> Exported =>
        typeof(TopCommand).Assembly.GetExportedTypes();

    [Fact]
    public void Nothing_in_the_terminal_is_public_except_the_command_that_starts_it()
    {
        // TopCommand is public because Program.cs calls it. Everything else is a view, a dialog or
        // a decision that belongs to this surface, and the moment one of them is public it is
        // something a future change has to keep working.
        var offenders = Exported
            .Where(type => type.Namespace == typeof(TopCommand).Namespace)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["TopCommand"], offenders);
    }

    [Fact]
    public void The_command_exposes_one_way_in_and_no_more()
    {
        // Its other members are internal so tests can drive the surface without a run loop. What
        // is public is what the entry point calls.
        var offered = typeof(TopCommand)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["RunAsync"], offered);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the assembly really does export types, so an empty answer above would mean
        // the filter is wrong rather than that the surface is clean.
        Assert.NotEmpty(Exported);
        Assert.Contains(Exported, type => type.Name == nameof(TopCommand));
    }

    [Fact]
    public void Every_region_carries_the_title_the_status_line_calls_it()
    {
        // contracts/surface.md: a test finds a view by its title and asserts against the constant
        // rather than a copy of the words. That only holds while the constant is what the view is
        // actually built with.
        Assert.Equal(ProjectsRegion.RegionTitle, new ProjectsRegion().Title);
        Assert.Equal(AgentsRegion.RegionTitle, new AgentsRegion().Title);
        Assert.Equal(ConversationsRegion.RegionTitle, new ConversationsRegion().Title);
        Assert.Equal(TranscriptRegion.RegionTitle, new TranscriptRegion().Title);
    }

    [Fact]
    public void The_words_a_destructive_choice_offers_are_the_ones_it_acts_on()
    {
        // Both lists put the safe answer first and both are read by the code that acts on the
        // answer, so a reordering cannot make Enter destroy something.
        Assert.Equal(Removing.Keep, Removing.Choices[0]);
        Assert.Equal(Withdrawing.Keep, Withdrawing.Choices[0]);

        Assert.Contains(Removing.Destroy, Removing.Choices);
        Assert.Contains(Withdrawing.Retire, Withdrawing.Choices);
        Assert.Contains(Withdrawing.Purge, Withdrawing.Choices);
    }
}
