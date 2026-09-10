using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That a conversation can be started from the list of conversations.
/// </summary>
/// <remarks>
/// <para>
/// **Reported by the owner, who went looking in the obvious place and did not find it.** Both surfaces
/// could only compose from inside an existing conversation: the terminal offered asking and telling
/// from the transcript region alone, and the console only from a conversation's own page. So a project
/// with no conversations yet had no standing place to begin, and the first conversation was the
/// hardest one to start.
/// </para>
/// <para>
/// <c>Shell.Actions</c> states the rule this broke, directly above itself: <i>actions belong to the
/// region that shows the thing acted on</i>. A conversation that does not exist yet is not shown in a
/// transcript of one that does. The giveaway was in the form's own defaults — asking always suggested
/// a <i>new</i> conversation name, so it had always been a start-a-conversation action, filed under
/// the one region implying the opposite.
/// </para>
/// </remarks>
public sealed class StartingAConversationTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public StartingAConversationTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        // Somebody to speak to. Without one the picker has nothing to offer and says so, which is
        // its own case below rather than the shape these two are about.
        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("acme"), "unused.db", _broker.Address, null, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }

    [Fact]
    public async Task The_conversations_region_offers_starting_one()
    {
        // The whole report: somebody seated, looking at the list, wanting to begin.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        // The conversations pane, which is where somebody stands when they want to begin.
        while (shell.Panes[shell.FocusedRegion] is not ConversationsRegion)
        {
            shell.Move(forwards: true);
        }

        var offered = shell.Actions().Select(action => action.Label).ToArray();

        Assert.Contains(Participation.StartTitle, offered);
        Assert.Contains(Participation.StartTellTitle, offered);
    }

    [Fact]
    public async Task Starting_one_suggests_a_new_name_rather_than_the_open_conversation()
    {
        // The difference between starting and continuing is the name the form arrives holding, and
        // that is the whole of it. From the list it must not be the conversation somebody happens to
        // have selected, or "start" quietly means "add to that one".
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.AskTitle, Orders.Value, () => shell.Settled);

        // The conversation name is the last field, and it arrives suggested rather than empty.
        Assert.NotEqual(string.Empty, form.Values[^1]);

        form.Finish(taken: false);
        await shell.Settled;
    }

    [Fact]
    public async Task Telling_from_the_list_starts_one_too()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        var form = await Composing.WithAsync(
            driving, shell.StartByTelling, Participation.TellTitle, Orders.Value, () => shell.Settled);

        Assert.NotEqual(string.Empty, form.Values[^1]);

        form.Finish(taken: false);
        await shell.Settled;
    }

    [Fact]
    public async Task A_project_with_nobody_in_it_says_so_rather_than_opening_an_empty_picker()
    {
        // A list with nothing in it is a surface that looks broken; a sentence naming what is
        // missing is one somebody can act on.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([], NodeCatalogue.Empty, null);

        await shell.Start();
        await shell.Settled;

        Assert.Equal(Participation.NoPeers, shell.Said);
    }

    [Fact]
    public async Task Without_a_seat_the_list_offers_neither()
    {
        // The control, and the same rule every other way of speaking is held to: without a seat there
        // is no identity to speak as, so the region shows the list and offers no voice to use in it.
        using var driving = Driving.Open();
        using var directory = Directory();

        var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        // The conversations pane, which is where somebody stands when they want to begin.
        while (shell.Panes[shell.FocusedRegion] is not ConversationsRegion)
        {
            shell.Move(forwards: true);
        }

        var offered = shell.Actions().Select(action => action.Label).ToArray();

        Assert.DoesNotContain(Participation.StartTitle, offered);
        Assert.DoesNotContain(Participation.StartTellTitle, offered);

        shell.Dispose();
    }
}
