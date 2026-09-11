using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That a field holding prose is several rows deep, and follows what is written into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner after writing a long notification.</b> Every field on this surface was
/// one row tall, which shows about sixty characters — and rule 8.5 lets a notification carry eight
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
