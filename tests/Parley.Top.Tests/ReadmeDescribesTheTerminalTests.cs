using Parley.Client.Views;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the README describes this surface and every action its screens offer.
/// </summary>
/// <remarks>
/// <para>
/// Found by lens 3 on 006: the README mentioned `parley top` once, in passing, with the whole
/// surface undocumented in the one document that has to stand alone. Held to the surface's own
/// labels, so an action added to a menu is an action the README must say.
/// </para>
/// <para>
/// Here rather than beside the CLI's help agreement, which is where it was written. It names the
/// terminal's own titles, and those are in this assembly now — a check about a surface, kept
/// where the surface is, so moving one without the other fails the build rather than the review.
/// </para>
/// </remarks>
public sealed class ReadmeDescribesTheTerminalTests
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

    [Fact]
    public void The_readme_describes_the_terminal_and_what_its_screens_offer()
    {
        // Found by lens 3: the README mentioned `parley top` once, in passing, with the whole
        // surface undocumented in the one document that has to stand alone. Held to the
        // surface's own labels, so an action added to a menu is an action the README must say.
        var readme = Readme();

        Assert.Contains("### From a terminal", readme, StringComparison.Ordinal);
        Assert.Contains("parley top", readme, StringComparison.Ordinal);

        foreach (var offered in new[]
                 {
                     ("create a new project", Naming.CreateTitle),
                     ("name the deployment this machine already runs", Naming.NameRunningTitle),
                     ("a project can be removed", Removing.Title),
                     ("typed back", Removing.Destroy),
                     ("a seat", AgentsRegion.JoinTitle),

                     // 005 added the two the README had no reason to describe before, and each
                     // is the half of "who is in a project" the terminal could not touch.
                     ("add an agent", Adding.Title),
                     ("remove one", Withdrawing.Title),

                     // 006 reworded the two states this used to name, because the owner read both
                     // on screen and could not say what either meant. What the README has to carry
                     // is the wording a person will actually see.
                     (Standing.NeverRun, Standing.NeverRun),
                     (Standing.OnlyWhenOpened,
                      Standing.OnlyWhenOpened),

                     // And the three things 006 added to the surface.
                     ("archive", Shell.ArchiveTitle),
                     ("destroy", Destroying.Title),
                     ("F8", "the browser key"),
                 })
        {
            Assert.True(
                readme.Contains(offered.Item1, StringComparison.OrdinalIgnoreCase),
                $"the README does not describe '{offered.Item2}' (looked for '{offered.Item1}').");
        }
    }
}
