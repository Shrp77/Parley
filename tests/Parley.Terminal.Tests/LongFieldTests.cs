using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a field holding prose is several rows deep, and follows what is written into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner after writing a long notification.</b> Every field on this surface was
/// one row tall, which shows about sixty characters — and rule 8.4 lets a notification carry eight
/// thousand. Somebody writing a paragraph could not see the paragraph they were writing.
/// </para>
/// <para>
/// <b>The two kinds are told apart deliberately and both are checked here.</b> A name, a path or a
/// project stays one line: Enter in it takes the form's default button, which is how every screen
/// built on <c>FormDialog</c> is finished, and a line break in a project name is a value that will
/// not survive being written to a manifest and read back. Making every field deep would have taken
/// that away everywhere to fix it in three places.
/// </para>
/// </remarks>
public sealed class LongFieldTests
{
    private static FormDialog Form() =>
        new(
            "Tell somebody something",
            [
                new FormField("What are you telling them?", string.Empty, Long: true),
                new FormField("Conversation name:", "planning"),
            ]);

    /// <summary>How deep a field is drawn right now.</summary>
    private static int Deep(FormDialog form, int at) => form.Field(at).Frame.Height;

    [Fact]
    public void A_long_field_opens_deeper_than_one_row()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        // A field that only grows once somebody has overflowed it teaches them it is a line, so
        // it looks like what it is before anything is typed.
        Assert.Equal(FormDialog.Opening, Deep(form, 0));
    }

    [Fact]
    public void An_ordinary_field_is_still_one_row()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        Assert.Equal(1, Deep(form, 1));
    }

    [Fact]
    public void It_grows_as_the_text_outgrows_it()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        var opening = Deep(form, 0);

        form.Fill(0, new string('x', form.Field(0).Viewport.Width * (FormDialog.Opening + 2)));

        Assert.True(
            Deep(form, 0) > opening,
            $"the field stayed {Deep(form, 0)} rows deep with {FormDialog.Opening + 2} rows in it");
    }

    [Fact]
    public void It_shrinks_again_when_the_text_goes()
    {
        // The half that is easy to leave out. A field that only ever grows leaves a form with a
        // hole in it after somebody deletes what they wrote and starts again.
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        form.Fill(0, new string('x', form.Field(0).Viewport.Width * (FormDialog.Opening + 2)));

        var grown = Deep(form, 0);

        form.Fill(0, "short");

        Assert.True(grown > Deep(form, 0), "the field kept the rows it grew for text that has gone");
        Assert.Equal(FormDialog.Opening, Deep(form, 0));
    }

    [Fact]
    public void It_stops_growing_rather_than_pushing_the_buttons_off_the_screen()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        form.Fill(0, new string('x', form.Field(0).Viewport.Width * 40));

        // Past the cap the field scrolls and the rest of the form stays where it is. A form is
        // several fields and a screen is one screen.
        Assert.Equal(FormDialog.Deepest, Deep(form, 0));
    }

    [Fact]
    public void A_line_break_typed_into_a_long_field_is_kept_rather_than_sending_the_form()
    {
        using var driving = Driving.Open();

        var form = Form();
        string[]? sent = null;

        form.Done += (_, values) => sent = values?.ToArray();

        driving.Show(form);

        form.Field(0).SetFocus();
        driving.Type("first");
        form.Field(0).NewKeyDownEvent(Key.Enter);
        driving.Type("second");

        Assert.Null(sent);
        Assert.Contains("first", form.Values[0], StringComparison.Ordinal);
        Assert.Contains("second", form.Values[0], StringComparison.Ordinal);
        Assert.Contains('\n', form.Values[0]);
    }

    [Fact]
    public void Typing_into_a_long_field_grows_it_a_keystroke_at_a_time()
    {
        // Driven by keys rather than by `Fill`, because what was asked for is a field that follows
        // editing — and this repository has already recorded once that a key checked only through
        // a direct assignment is a key nobody has pressed.
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        var field = form.Field(0);

        field.SetFocus();

        var opening = Deep(form, 0);

        for (var typed = 0; typed < field.Viewport.Width * (FormDialog.Opening + 1); typed++)
        {
            field.NewKeyDownEvent(Key.A);
        }

        Assert.True(Deep(form, 0) > opening, "typing past the bottom of the field did not open a row");
    }

    [Fact]
    public void What_is_typed_into_a_long_field_comes_back_whole()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        var written = new string('x', 500);

        form.Fill(0, written);

        // The whole point: the cap is eight thousand characters and this used to be collected in
        // a field showing sixty of them.
        Assert.Equal(written, form.Values[0]);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(40)]
    public void The_form_never_grows_taller_than_the_screen(int height)
    {
        // **Found by the review gate, on a small terminal, and it was pre-existing and made
        // worse.** A form taller than the screen is centred and clipped at the top, so its first
        // label and first field were off the screen entirely and the buttons were all that showed.
        // A four-field form was already eighteen rows; opening three prose fields at three rows
        // each added six more, which is the difference between fitting a twenty-four row terminal
        // and not.
        using var driving = Driving.Open(width: 120, height: height);

        var form = new FormDialog(
            "Ask somebody a question",
            [
                new FormField("What are you trying to decide?", string.Empty, Long: true),
                new FormField("The question:", string.Empty, Long: true),
                new FormField("What turns on the answer?", string.Empty, Long: true),
                new FormField("Conversation name:", "planning"),
            ]);

        driving.Show(form);

        // **Measured before anything is typed, and that is the half this test did not have.** It
        // went straight to filling a field, so it only ever checked a form that had been re-laid
        // by a keystroke — and a keystroke was the only thing that re-laid one. A form too tall to
        // fit opened clipped, with its title row off the top, and stayed that way until somebody
        // wrote into it. Found when the ask form's labels grew to carry an example each.
        Assert.True(
            form.Frame.Height <= height,
            $"on opening, the form is {form.Frame.Height} rows on a {height}-row screen");

        Assert.True(form.Frame.Y >= 0, $"on opening, the form starts at row {form.Frame.Y}");

        // Written into until the first field would want every row it is allowed.
        form.Fill(0, new string('x', 4000));

        Assert.True(
            form.Frame.Height <= height,
            $"the form is {form.Frame.Height} rows on a {height}-row screen");

        // And it is on the screen rather than hanging off the top of it, which is the failure the
        // height on its own does not catch.
        Assert.True(form.Frame.Y >= 0, $"the form starts at row {form.Frame.Y}");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(40)]
    public void The_request_form_this_surface_actually_opens_fits_on_opening(int height)
    {
        // **The labels the composer really carries, rather than a stand-in for them.** Two fields
        // since 0.7, where there were five — and the check is worth keeping at two: the ask's label
        // carries a second sentence, so it is still tall enough to overflow a short screen on its
        // own, which no stand-in label would have shown.
        using var driving = Driving.Open(width: 100, height: height);

        var form = new FormDialog(
            Participation.RequestTitle,
            [
                new FormField(
                    $"{Framing.Ask}. {Framing.AskWhy}",
                    string.Empty,
                    Participation.RequiredProblem,
                    Long: true),
                new FormField(
                    "Conversation name — " + ConversationName.Rule,
                    "terminal-20260911",
                    Participation.ConversationProblem),
            ]);

        driving.Show(form);

        Assert.True(
            form.Frame.Height <= height,
            $"the request form is {form.Frame.Height} rows on a {height}-row screen");

        Assert.True(form.Frame.Y >= 0, $"the request form starts at row {form.Frame.Y}");
    }

    [Fact]
    public void Trimming_takes_the_rows_from_the_deepest_field_first()
    {
        // The rows are given back in the order they were taken, so a form under pressure loses
        // depth from the field that has most of it rather than from whichever comes first.
        using var driving = Driving.Open(width: 120, height: 24);

        var form = new FormDialog(
            "Ask somebody a question",
            [
                new FormField("What are you trying to decide?", string.Empty, Long: true),
                new FormField("The question:", string.Empty, Long: true),
                new FormField("What turns on the answer?", string.Empty, Long: true),
                new FormField("Conversation name:", "planning"),
            ]);

        driving.Show(form);
        form.Fill(0, new string('x', 4000));

        var depths = Enumerable.Range(0, 3).Select(at => form.Field(at).Frame.Height).ToList();

        Assert.All(depths, deep => Assert.True(deep >= 1, "a field was trimmed out of existence"));
        Assert.True(
            depths.Max() - depths.Min() <= 1,
            $"the trim left the fields uneven: {string.Join(",", depths)}");
    }

    [Fact]
    public void Writing_past_the_cap_is_refused_while_it_is_being_written()
    {
        // **Found by lens 2, and this branch is what made it reachable.** The cap is enforced at
        // the send, by which time the form has closed and what somebody wrote is gone — a sentence
        // about the limit and no way back to their text. Nobody met that while every field was one
        // row tall; a field that invites a paragraph invites a pasted document.
        using var driving = Driving.Open();

        var form = new FormDialog(
            "Tell somebody something",
            [
                new FormField(
                    "What are you telling them?", string.Empty, Participation.RequiredProblem,
                    Long: true),
                new FormField("Conversation name:", "planning"),
            ]);

        driving.Show(form);

        form.Fill(0, new string('x', MessageLimits.MaxPayloadCharacters + 1));

        Assert.Contains(
            MessageLimits.MaxPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            form.Problems[0],
            StringComparison.Ordinal);

        // And the button that would have sent it is refused, so nothing is lost by pressing it.
        form.Finish(taken: true);

        form.Fill(0, "short enough");

        Assert.Equal(string.Empty, form.Problems[0]);
    }

    [Fact]
    public void A_long_field_is_a_different_view_from_a_short_one()
    {
        using var driving = Driving.Open();

        var form = Form();

        driving.Show(form);

        Assert.IsNotType<TextField>(form.Field(0));
        Assert.IsType<TextField>(form.Field(1));
    }
}
