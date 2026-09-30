using Parley.Client;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every field with a rule about characters applies it as it is typed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report, and the general instruction that came with it.</b> They named a
/// conversation <c>tell everybody test</c>, wrote the message, sent it, and were told what a name
/// may hold with the form closed and their words gone — and asked that every field with a limit on
/// its characters refuse them at the keystroke and say so, rather than at the end.
/// </para>
/// <para>
/// Three fields were in that position: the conversation name on the composer, the role an enrolled
/// repository answers to, and the name somebody joins under. The project name was already correct
/// as it was typed, and is what the other three were made to match —
/// <see cref="NameAsYouTypeTests"/> is its own record.
/// </para>
/// <para>
/// Driven rather than reasoned, for the reason that file gives: correcting means setting a field's
/// text from inside its own changed event, and nothing short of typing into it finds out what the
/// toolkit does with the caret afterwards.
/// </para>
/// </remarks>
public sealed class FieldsCorrectAsTypedTests
{
    private static void Type(Driving.Session driving, string text)
    {
        foreach (var key in text)
        {
            driving.Press(new Key(key));
        }
    }

    [Fact]
    public void A_conversation_name_takes_the_owners_own_words()
    {
        // The report itself: `tell everybody test` reaches the field as a name it will accept,
        // rather than as a sentence refused after the message has been written.
        using var driving = Driving.Open();

        var form = new FormDialog(
            Participation.RequestTitle,
            [
                new FormField(
                    "Conversation name:",
                    string.Empty,
                    Participation.ConversationProblem,
                    Correct: ConversationName.Correct,
                    Corrected: ConversationName.WasCorrected),
            ]);

        driving.Show((View)form);

        Type(driving, "tell everybody test");

        Assert.Equal("tell-everybody-test", form.Values[0]);
        Assert.Null(Participation.ConversationProblem(form.Values[0]));
        Assert.Contains(ConversationName.WasCorrected, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void What_correcting_cannot_fix_is_still_said()
    {
        // Correcting does not replace the message, and the two must not overlap: a run of dots is
        // left alone -- collapsing somebody's dots is deciding what they meant -- so the sentence
        // is the only thing standing between that name and a refusal at the send.
        using var driving = Driving.Open();

        var form = new FormDialog(
            Participation.RequestTitle,
            [
                new FormField(
                    "Conversation name:",
                    string.Empty,
                    Participation.ConversationProblem,
                    Correct: ConversationName.Correct,
                    Corrected: ConversationName.WasCorrected),
            ]);

        driving.Show((View)form);

        Type(driving, "a..b");

        Assert.Equal("a..b", form.Values[0]);
        Assert.NotNull(Participation.ConversationProblem(form.Values[0]));
        Assert.Contains("cannot name a conversation", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_enrolled_agents_role_is_corrected_as_it_is_typed()
    {
        // The same defect in the field beside it: `Order Book` met no resistance until the form
        // was finished, and the rule it breaks is one every peer then addresses it by.
        using var driving = Driving.Open();

        var form = new FormDialog(
            Adding.Title,
            [
                new FormField(
                    "The role peers will address it by:",
                    string.Empty,
                    Adding.RoleProblem,
                    Correct: EnrollmentPlan.CorrectName,
                    Corrected: EnrollmentPlan.NameWasCorrected),
            ]);

        driving.Show((View)form);

        Type(driving, "Order Book");

        Assert.Equal("order-book", form.Values[0]);
        Assert.Null(Adding.RoleProblem(form.Values[0]));
    }

    // Two tests of the seat's Join field were here — a name corrected as typed, and one the correction cannot save.
    // specs/023 removed the field: nothing is typed to take a seat. The correction machinery in `Prompting.Text` has no
    // caller now either; it is left in place and recorded in tasks.md rather than removed inside this change.

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(
                ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
