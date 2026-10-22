using Parley.Client;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a project name is made acceptable as it is typed, and that the form saying so fits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves come from one report.</b> The owner typed <c>OrderBook</c> into the create
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
    // **`Order Book` was `orderbook` and is `order-book` now**, which is the one thing about
    // this correction that changed after 0.25.1. The owner chose the hyphen for the conversation
    // field on the grounds that `telleverybodytest` is a name nobody can read back, and the field
    // beside it had no business going on swallowing spaces. One grammar, one correction:
    // `Parley.Core.NameCorrection` is what a project's name, an enrolled agent's role and a seat
    // are all held to now.
    [Theory]
    [InlineData("OrderBook", "orderbook")]
    [InlineData("Order Book", "order-book")]
    [InlineData("Order  Book", "order-book")]
    [InlineData("ORDER_BOOK", "orderbook")]
    [InlineData("order-book", "order-book")]
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

        foreach (var key in "OrderBook")
        {
            driving.Press(new global::Terminal.Gui.Input.Key(key));
        }

        Assert.Equal("orderbook", form.Values[0]);
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
            driving.Press(new global::Terminal.Gui.Input.Key(key));
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
            driving.Press(new global::Terminal.Gui.Input.Key(key));
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
        // are sentences rather than words. The reason somebody would fill a field in is the part
        // that went missing, which is the half that matters: a rule nobody can read is a rule the
        // field does not have.
        //
        // The label this was written against was the node catalogue's, at a hundred and ten
        // characters; 019 removed that field, so it asks the same question of the name's.
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

        // **Repointed rather than deleted when 019 took the catalogue field.** What this guards is
        // that a field's label reaches the screen whole, which 0.18.11 paid for: the rule saying a
        // project name is lowercase sat on a clipped row, so the owner typed a capital and met no
        // resistance at all. The label happened to be the longest one; the property is about any of
        // them, and the name field's is the one whose words matter most for the same reason.
        var missing = "a label for this machine only, never sent anywhere"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(',', '.'))
            .Where(word => !screen.Contains(word, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"at {width}x{height}, {missing.Count} words of the name field's label are off "
            + $"screen: {string.Join(" ", missing.Take(12))}");
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null)],
            _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
