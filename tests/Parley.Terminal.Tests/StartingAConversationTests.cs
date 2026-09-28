using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a person looking for how to start a conversation is shown it, and is not misled by the one thing they found
/// instead.
/// </summary>
/// <remarks>
/// Reported by the owner with a screenshot: the conversations menu offered no way to start one, because the action is
/// hidden until a seat is held and nothing said so; the empty list's own sentence was cut at the pane's edge before it
/// said how; and Instruct, which they reached for, answered that the agent's machine was not connected when the machine
/// was and the agent simply was not running.
/// </remarks>
public sealed class StartingAConversationTests
{
    [Fact]
    public async Task Without_a_seat_the_conversations_menu_still_offers_to_start_one_and_says_what_it_takes()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Conversations));

        Assert.Null(shell.Held);

        var start = Assert.Single(shell.Actions(), action => action.Key == 'k');

        Assert.Equal(Participation.StartSeatFirstTitle, start.Label);
    }

    [Fact]
    public async Task Choosing_it_without_a_seat_says_why_and_asks_for_a_name()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        await shell.StartSeatFirst();

        Assert.Equal(Participation.SeatFirst, shell.Said);
    }

    [Fact]
    public void Every_row_of_the_empty_list_fits_the_narrowest_pane()
    {
        // Forty columns is the narrowest terminal this surface is held to, less the pane's two border columns.
        Assert.All(ConversationsRegion.Nothing, row => Assert.True(row.Length <= 38, $"{row.Length}: {row}"));
        Assert.Contains(ConversationsRegion.Nothing, row => row.Contains("press k", StringComparison.Ordinal));
    }

    [Fact]
    public void Undeliverable_names_both_causes_and_what_to_do()
    {
        Assert.Contains("not running", InstructionWording.Undeliverable, StringComparison.Ordinal);
        Assert.Contains("not connected", InstructionWording.Undeliverable, StringComparison.Ordinal);
        Assert.Contains("request", InstructionWording.Undeliverable, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_titles_the_terminal_with_its_build_and_a_dialog_does_not_take_the_title()
    {
        using var directory = Directory();
        using var shell = new Shell(directory);

        Assert.Equal($"parley {BuildIdentity.Version}", shell.Title);

        using var driving = Driving.Open();
        var dialog = Prompting.Choose("What this region can do", "described", ["one"]);

        // Non-vacuity: the toolkit's dialogs do take the terminal's title unless told not to.
        Assert.True(dialog.Border.Settings.HasFlag(BorderSettings.TerminalTitle));

        var token = Prompting.Open(driving.App, dialog);

        Assert.False(dialog.Border.Settings.HasFlag(BorderSettings.TerminalTitle));

        Prompting.Close(driving.App, token);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
