using Parley.Cli.Top;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That nothing can repaint the description standing between a person and a destructive act.
/// </summary>
/// <remarks>
/// <para>
/// Every destructive act on this surface is behind a description and the thing's own name typed
/// back. The typed name is the deliberate act; <b>the description is the informed half</b>, and it
/// is worth nothing if whatever it quotes can rewrite it.
/// </para>
/// <para>
/// <b>The removal door quoted a manifest raw.</b> A manifest is a file the person may not have
/// written — <c>ProjectsRegion</c> has neutralised the same four values on their way to a row
/// since it was written — and this dialog took the endpoint, the store and the catalogue path
/// straight through. An escape sequence in any of them clears the description somebody is meant to
/// read before typing a project's name to destroy it.
/// </para>
/// <para>
/// Found by a lens asking which strings reach a dialog rather than a row, which is a question the
/// per-region tests do not ask: they check the rows.
/// </para>
/// </remarks>
public sealed class DoorsAreReadableTests
{
    /// <summary>What a hostile value tries to do to the screen.</summary>
    private static readonly char Escape = (char)27;

    [Fact]
    public void The_removal_description_cannot_be_repainted_by_the_manifest_it_quotes()
    {
        var described = Removing.Describe(Hostile());

        Assert.DoesNotContain(Escape, described);

        // Four lines, and a value cannot make a fifth. The line breaks in the description are this
        // surface's own; a carriage return or newline inside a manifest value is replaced rather
        // than obeyed, so it cannot add a row that looks like another field.
        Assert.Equal(4, described.ReplaceLineEndings("\n").Split('\n').Length);
    }

    [Fact]
    public void It_still_says_which_project_and_what_would_go_with_it()
    {
        // The other half: neutralising must not neutralise the point. Somebody deciding whether to
        // destroy a project needs to see which store and which catalogue go with it.
        var described = Removing.Describe(Hostile());

        Assert.Contains("alpha", described, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", described, StringComparison.Ordinal);
        Assert.Contains("store", described, StringComparison.Ordinal);
        Assert.Contains("config.json", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_with_no_catalogue_says_so_rather_than_showing_a_blank()
    {
        // The branch the fix rewrote. An empty catalogue path has to keep reading as "nothing is
        // started for it" rather than as a field that failed to render.
        var described = Removing.Describe(new ProjectSettings(
            ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null, null));

        Assert.Contains("nothing is started for it", described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the fixture has to actually carry the characters being defended against.
        var project = Hostile();

        Assert.Contains(Escape, project.Endpoint);
        Assert.Contains(Escape, project.Store!);
        Assert.Contains(Escape, project.NodeConfig!);
    }

    /// <summary>A manifest entry that tries to rewrite the dialog quoting it.</summary>
    /// <returns>The project.</returns>
    private static ProjectSettings Hostile() =>
        new(
            ProjectName.Create("alpha"),
            $"C:/store{Escape}[31m.db",
            $"http://127.0.0.1:1{Escape}[2J",
            $"C:/node{Escape}[2Jconfig.json",
            Description: null);
}
