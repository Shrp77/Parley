using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That an underscore reaches the screen, in every place this surface draws somebody else's text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner, in the middle of a different report.</b> They were shown the rule for
/// naming a conversation and it read <c>letters, digits, '.', '' and '-'</c> — one of the rule's
/// own characters missing, in the sentence explaining a refusal. The toolkit's
/// <c>Label</c> takes <c>_</c> as the marker for a hotkey and removes it on the way to the screen.
/// </para>
/// <para>
/// <b>A conversation identifier may hold one, and so may a path, a project's description and a
/// broker's own words.</b> That makes this the same class of defect as the ones
/// <c>Safe</c> exists for, from the other direction: what is drawn has to be what is
/// there, or somebody is deciding about a value they have not seen.
/// </para>
/// <para>
/// Read from the drawn screen rather than from a property, because the property was never the
/// thing that was wrong: <c>Said</c> held the underscore throughout.
/// </para>
/// </remarks>
public sealed class UnderscoresSurviveTests
{
    [Fact]
    public void The_message_line_keeps_one()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report("named release_1 and it went");

        driving.App.LayoutAndDraw(true);

        Assert.Contains("release_1", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_keeps_one_in_its_label_and_in_what_it_says_about_the_field()
    {
        // Both halves, because they are two labels: the rule above the field names `'_'` as an
        // allowed character, and the refusal below it quotes what was typed.
        using var driving = Driving.Open();

        var form = new FormDialog(
            "Name it",
            [
                new FormField(
                    "Conversation name — " + ConversationName.Rule,
                    "release_1..2",
                    Participation.ConversationProblem),
            ]);

        driving.Show((View)form);

        var drawn = driving.Text();

        Assert.Contains("'_'", drawn, StringComparison.Ordinal);
        Assert.Contains("release_1..2", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dialog_keeps_one_in_the_question_it_asks()
    {
        // `TextDialog`'s question is where the destroy warning goes, and that names a store path
        // read out of somebody's manifest.
        using var driving = Driving.Open();

        var dialog = Prompting.Text(
            "Destroy it", "This deletes C:/stores/order_book.db and everything in it.");

        driving.Show((View)dialog);

        Assert.Contains("order_book.db", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_keeps_one_in_the_description_it_is_decided_from()
    {
        using var driving = Driving.Open();

        var choosing = Prompting.Choose(
            "Remove it", "The project order_book, and its store.", ["Keep it", "Destroy it"]);

        driving.Show((View)choosing);

        Assert.Contains("order_book", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_row_keeps_one()
    {
        // The regions draw rows rather than labels, and a conversation identifier is the row.
        // Checked rather than assumed: a list that ate them would put the defect back in the one
        // place a name is actually read.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Conversations.Show(
            [
                new ConversationSummary(
                    "release_1",
                    [AgentName.Create("orders@test")],
                    1,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    HasOpenExchange: true,
                    Archived: false),
            ],
            null);

        driving.App.LayoutAndDraw(true);

        Assert.Contains("release_1", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_label_on_the_surface_has_hotkeys_off()
    {
        // **The drawn-text checks above catch the labels that exist; this catches the next one.**
        // A `new Label` added to a dialog a year from now eats underscores again and no test
        // would be looking at it — so the property is asserted of the tree rather than of five
        // particular sentences.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        _ = shell.Offer();

        var plain = new List<string>();

        Walk(driving.App.TopRunnableView, plain);

        Assert.True(plain.Count == 0, "these labels still eat an underscore: " + string.Join(", ", plain));

        static void Walk(View? view, List<string> found)
        {
            if (view is null)
            {
                return;
            }

            if (view is Label label && label.HotKeySpecifier != Plain.NoHotKey)
            {
                found.Add($"{label.GetType().Name}('{label.Text}')");
            }

            foreach (var child in view.SubViews)
            {
                Walk(child, found);
            }
        }
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
