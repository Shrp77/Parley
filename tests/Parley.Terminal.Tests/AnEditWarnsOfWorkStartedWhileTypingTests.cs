using Terminal.Gui.Input;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That an edit in the terminal is checked for open work it removes against the roadmap as it is when saved.
/// </summary>
/// <remarks>
/// <b>Found by review, round 22.</b> The edit was checked against the roadmap read when the form opened, so work started
/// while the person typed was removed with no warning; starting an item moves no revision. The edit reads the roadmap
/// again first (<c>Revising.StillCoveredAsync</c>). The going-ahead, stale, leave and unreadable cases held.
/// </remarks>
public sealed class AnEditWarnsOfWorkStartedWhileTypingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public AnEditWarnsOfWorkStartedWhileTypingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_connection);

    private BrokerClient Client => new(_broker.Operators);

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    [Fact]
    public async Task Work_started_while_the_edit_form_is_open_is_not_removed_unwarned()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            T0,
            cancellationToken: Ct);
        Assert.Null(problem);
        var first = await Repository.GetProposalAsync(id!.Value, Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Edit();
        await shell.Settled;

        // While the person is typing, nothing is open; item 1 is started elsewhere before they save.
        var person = (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => PersonSeat.Is(a.Name)).Name;
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await speaking.StartAsync(person, T0, Ct);
        var opened = await speaking.RequestAsync(person, Billing, "model-a", """{"ask":"Do the item."}""", T0, "rm1-1", Ct);
        Assert.NotNull(opened.Exchange);

        var form = (FormDialog)driving.Find(Revising.Title);
        form.Fill(0, "title: Billing export\n1. Something else entirely | for: billing@test");
        form.Finish(taken: true);
        await shell.Settled;

        var roadmap = Assert.Single(await Client.RoadmapsAsync(Ct)).Id;
        var item1 = (await Client.RoadmapAsync(roadmap, Ct))!.Items.Single(i => i.Number == 1);
        Assert.False(
            item1.Removed,
            $"item 1's open work was removed with no warning: the edit was checked against the roadmap read when the form opened. Said: {shell.Said}");
    }

    private async Task<(Shell Shell, ChoiceDialog Asking)> WarnedAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };
        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var (id, problem) = await Repository.ProposeAsync(
            Orders, null, null,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            T0, cancellationToken: Ct);
        Assert.Null(problem);
        var first = await Repository.GetProposalAsync(id!.Value, Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        var person = (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => PersonSeat.Is(a.Name)).Name;
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await speaking.StartAsync(person, T0, Ct);
        Assert.NotNull((await speaking.RequestAsync(person, Orders, "model-a", """{"ask":"Do the item."}""", T0, "rm1-2", Ct)).Exchange);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        Assert.IsType<PlanView>(driving.App.TopRunnableView).Edit();
        await shell.Settled;

        var form = (FormDialog)driving.Find(Revising.Title);
        form.Fill(0, "title: Billing export\n1. Add CSV export | for: billing@test | item: 1");
        form.Finish(taken: true);
        await shell.Settled;

        var asking = (ChoiceDialog)driving.Find(Revising.Title);
        Assert.Contains("item 2", asking.Described, StringComparison.Ordinal);
        return (shell, asking);
    }

    [Fact]
    public async Task Going_ahead_after_the_roadmap_was_revised_meanwhile_is_refused_as_stale()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        var (shell, asking) = await WarnedAsync(driving, directory);
        using var _ = shell;

        var roadmap = Assert.Single(await Repository.ListAsync(Ct));
        Assert.True((await Repository.EditAsync(
            roadmap.Id, roadmap.Revision,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(1, "Add CSV export, reworded elsewhere", Billing, []),
                new RoadmapItemDraft(2, "Call it from the report", Orders, [1]),
            ]),
            T0, cancellationToken: Ct)).Done);

        driving.Row(asking.Choices, Revising.RemoveAnyway);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var now = (await Client.RoadmapAsync(roadmap.Id, Ct))!;
        Assert.False(now.Items.Single(i => i.Number == 2).Removed, $"Said: {shell.Said}");
        Assert.Equal("Add CSV export, reworded elsewhere", now.Items.Single(i => i.Number == 1).Text);
        Assert.Equal(Revising.Stale, shell.Said);
    }

    [Fact]
    public async Task Leave_on_the_warning_changes_nothing()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        var (shell, asking) = await WarnedAsync(driving, directory);
        using var _ = shell;

        driving.Row(asking.Choices, Revising.Leave);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var roadmap = Assert.Single(await Client.RoadmapsAsync(Ct));
        Assert.False((await Client.RoadmapAsync(roadmap.Id, Ct))!.Items.Single(i => i.Number == 2).Removed);
        Assert.Equal(Revising.Left, shell.Said);
    }

    [Fact]
    public async Task Going_ahead_when_the_broker_cannot_be_read_saves_nothing()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        var (shell, asking) = await WarnedAsync(driving, directory);
        using var _ = shell;

        await _broker.DisposeAsync();

        driving.Row(asking.Choices, Revising.RemoveAnyway);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.StartsWith("The roadmap could not be read, so nothing was saved", shell.Said, StringComparison.Ordinal);
        Assert.False((await Repository.ListAsync(Ct)).Single().Revision > 1);
    }
}
