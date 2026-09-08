using Parley.Cli.Top;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That a project name is made acceptable as it is typed, and that the form saying so fits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves come from one report.</b> The owner typed <c>TimeTracker</c> into the create
/// form and met no resistance at all — no warning, no correction — and separately found the form
/// running off the screen. The two are the same failure: the only thing enforcing lowercase was a
/// sentence on a row that had been clipped, under a label that had itself overflowed.
/// </para>
/// <para>
/// So the rule is applied rather than only reported. A field that will not take a capital cannot
/// fail because of a layout, whatever the layout does — and the message still exists for what
/// correcting cannot fix.
/// </para>
/// </remarks>
public sealed class NameAsYouTypeTests
{
    [Theory]
    [InlineData("TimeTracker", "timetracker")]
    [InlineData("Time Tracker", "timetracker")]
    [InlineData("TIME_TRACKER", "timetracker")]
    [InlineData("time-tracker", "time-tracker")]
    [InlineData("Café/Bar!", "cafbar")]
    [InlineData("north-helm", "north-helm")]
    public void A_name_is_corrected_to_something_the_rule_accepts(string typed, string expected)
    {
        var corrected = Naming.NameCorrection(typed);

        Assert.Equal(expected, corrected);

        // And what comes out is genuinely acceptable, rather than merely different: the correction
        // and the rule must not disagree, or the field would keep rewriting text it then refuses.
        Assert.True(
            ProjectName.TryCreate(corrected, out _),
            $"'{typed}' was corrected to '{corrected}', which the rule still refuses");
    }

    [Theory]
    [InlineData("")]
    [InlineData("9lives")]
    [InlineData("-leading")]
    public void It_corrects_and_never_invents(string typed)
    {
        // A name that cannot be salvaged by dropping and lowering is left for the message to
        // refuse. Guessing what somebody meant is how a field comes to hold a name they did not
        // choose -- and this one names a database that gets created.
        var corrected = Naming.NameCorrection(typed);

        Assert.Equal(typed, corrected);
    }

    [Fact]
    public void Typing_a_name_one_key_at_a_time_leaves_it_the_right_way_round()
    {
        // **Driven rather than reasoned.** Correcting on every keystroke means setting the field's
        // text from inside its own changed event, and 2.4.17 exposes no caret to put back
        // afterwards. If the toolkit moved the cursor anywhere but the end, a name typed in would
        // come out reversed or interleaved. Nothing short of typing it finds that out.
        using var driving = Driving.Open();

        var form = new FormDialog(
            "Create a project",
            [
                new FormField(
                    "Project name:",
                    string.Empty,
                    typed => Naming.NameProblem(typed, []),
                    Correct: Naming.NameCorrection,
                    Corrected: Naming.NameWasCorrected),
            ]);

        driving.Show((View)form);

        foreach (var key in "TimeTracker")
        {
            driving.Press(new Terminal.Gui.Input.Key(key));
        }

        Assert.Equal("timetracker", form.Values[0]);
    }

    [Fact]
    public void The_correction_says_what_it_did()
    {
        // Nothing is rewritten silently. Somebody who typed a capital and got a lowercase letter
        // has to be told why, or they are left believing they typed something they did not.
        using var driving = Driving.Open();

        var form = new FormDialog(
            "Create a project",
            [
                new FormField(
                    "Project name:",
                    string.Empty,
                    typed => Naming.NameProblem(typed, []),
                    Correct: Naming.NameCorrection,
                    Corrected: Naming.NameWasCorrected),
            ]);

        driving.Show((View)form);

        foreach (var key in "Time")
        {
            driving.Press(new Terminal.Gui.Input.Key(key));
        }

        Assert.Contains(Naming.NameWasCorrected, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_needing_no_correction_says_nothing()
    {
        // The other side of it: a note that appeared for every name would be noise, and noise is
        // what somebody learns to read past.
        using var driving = Driving.Open();

        var form = new FormDialog(
            "Create a project",
            [
                new FormField(
                    "Project name:",
                    string.Empty,
                    typed => Naming.NameProblem(typed, []),
                    Correct: Naming.NameCorrection,
                    Corrected: Naming.NameWasCorrected),
            ]);

        driving.Show((View)form);

        foreach (var key in "walkthrough")
        {
            driving.Press(new Terminal.Gui.Input.Key(key));
        }

        Assert.DoesNotContain(Naming.NameWasCorrected, driving.Text(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(100, 30)]
    public async Task Every_word_of_the_create_form_reaches_the_screen(int width, int height)
    {
        // **The other half of the report.** FormDialog gave each label one row and counted three
        // rows per field, so a label longer than the dialog was cut where it ran out -- and these
        // are sentences: the node catalogue's is a hundred and ten characters, explaining that a
        // question to an agent nobody has open waits, times out and is ghosted. That is the reason
        // somebody would fill the field in, and it was the part that went missing.
        //
        // Driven through the real action rather than a stand-in form, so the labels under test are
        // the ones the surface actually shows.
        using var driving = Driving.Open(width, height);
        using var directory = Directory();
        using var shell = new Shell(directory)
        {
            ManifestPath = Path.Combine(Path.GetTempPath(), $"parley-{Guid.NewGuid():N}.json"),
        };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        var create = shell.Actions().Single(action => action.Label == Naming.CreateTitle);

        await create.Run();

        Assert.True(driving.Has(Naming.CreateTitle), "the create form did not open");

        var screen = driving.Text();

        var missing = "without one, a question to an agent nobody has open waits, times out and is ghosted"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(',', '.'))
            .Where(word => !screen.Contains(word, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"at {width}x{height}, {missing.Count} words of the catalogue field's label are off "
            + $"screen: {string.Join(" ", missing.Take(12))}");
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null, null)],
            _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
