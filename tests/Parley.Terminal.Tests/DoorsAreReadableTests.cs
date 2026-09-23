using Parley.Core;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

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

        // Three lines, and a value cannot make a fourth. The line breaks in the description are this
        // surface's own; a carriage return or newline inside a manifest value is replaced rather
        // than obeyed, so it cannot add a row that looks like another field. It was four until 019
        // took the catalogue line, which is a count this test has to carry rather than infer — an
        // assertion that counted whatever it was given would pass against the forgery it exists for.
        Assert.Equal(3, described.ReplaceLineEndings("\n").Split('\n').Length);
    }

    [Fact]
    public void It_still_says_which_project_and_what_would_go_with_it()
    {
        // The other half: neutralising must not neutralise the point. Somebody deciding whether to
        // destroy a project needs to see which store goes with it.
        var described = Removing.Describe(Hostile());

        Assert.Contains("alpha", described, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", described, StringComparison.Ordinal);
        Assert.Contains("store", described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_never_names_a_catalogue_destroying_does_not_touch()
    {
        // **This asked the opposite question until 019.** A project recorded a catalogue of its own
        // and destroying the project deleted it, so an absent one had to read as "nothing is started
        // for it" rather than as a field that failed to render. There is one catalogue per machine
        // now and this dialog does not touch it — so naming one here would say a thing was about to
        // go that is not, in the one dialog whose whole job is to say what destroying costs.
        var described = Removing.Describe(new ProjectSettings(
            ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null));

        Assert.DoesNotContain("catalogue", described, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData(120, 30)]
    [InlineData(80, 24)]
    public void Every_word_of_a_destructive_warning_reaches_the_screen(int width, int height)
    {
        // **The other half of a description doing its job, and the half that was missing.** The
        // tests above prove the text cannot be tampered with; none of them proved it could be
        // read. `TextDialog` gave its question a label with no width and no height, so whatever it
        // asked got exactly one row however long it was — and the longest thing it asks is the
        // destroy warning. Seventeen of its words never reached the screen, "Every conversation in
        // it goes with the store" among them, which is the single most important consequence of
        // pressing the button.
        //
        // Found by the owner trying to delete a project, not by this suite. Two sizes, because the
        // failure is about fitting and one size proves nothing about the other.
        using var driving = Driving.Open(width, height);

        var dialog = Prompting.Text(
            Removing.Destroy,
            Removing.Destroying + Environment.NewLine + Removing.TypeItBack,
            string.Empty,
            _ => null);

        driving.Show((View)dialog);
        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        var missing = Removing.Destroying
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('.', ','))
            .Where(word => !screen.Contains(word, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"at {width}x{height}, {missing.Count} words of the warning are off screen: "
            + string.Join(" ", missing.Take(12)));
    }

    [Theory]
    [InlineData(120, 30)]
    [InlineData(80, 24)]
    public void And_so_does_the_instruction_that_follows_it(int width, int height)
    {
        // The line telling somebody what to type is the one that makes the door usable at all. It
        // sits after the warning, so it is the first thing an overflow eats.
        using var driving = Driving.Open(width, height);

        var dialog = Prompting.Text(
            Removing.Destroy,
            Removing.Destroying + Environment.NewLine + Removing.TypeItBack,
            string.Empty,
            _ => null);

        driving.Show((View)dialog);
        driving.App.LayoutAndDraw(true);

        Assert.Contains("Type the project", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the fixture has to actually carry the characters being defended against.
        var project = Hostile();

        Assert.Contains(Escape, project.Endpoint);
        Assert.Contains(Escape, project.Store!);
    }

    /// <summary>A manifest entry that tries to rewrite the dialog quoting it.</summary>
    /// <returns>The project.</returns>
    private static ProjectSettings Hostile() =>
        new(
            ProjectName.Create("alpha"),
            $"C:/store{Escape}[31m.db",
            $"http://127.0.0.1:1{Escape}[2J",
            Description: null);
}
