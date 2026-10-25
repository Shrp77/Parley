using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That going ahead after a removal warning in the terminal covers only the open work the warning named.
/// </summary>
/// <remarks>
/// <b>Found by review, round 21.</b> The warning waits on a person, and work started on another item meanwhile was
/// removed with no word, for a revision and for an edit; starting an item moves no revision, so the expected revision
/// did not catch it. Going ahead now reads the roadmap again (as the web does) and changes nothing if more is open.
/// </remarks>
public sealed class ARevisionWarnedOfOpenWorkTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ARevisionWarnedOfOpenWorkTests()
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

    private async Task<long> ProposeAsync(string title = "Billing export", string? target = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            target,
            new RoadmapContent(title,
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            T0,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        return shell;
    }

    private static void Accept(Driving.Session driving, string title, string row)
    {
        var dialog = (ChoiceDialog)driving.Find(title);

        driving.Row(dialog.Choices, row);
        dialog.Choices.InvokeCommand(Command.Accept);
    }

    private async Task StartItemAsync(AgentName person, AgentName to, string conversation)
    {
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await speaking.StartAsync(person, T0, Ct);
        var opened = await speaking.RequestAsync(person, to, "model-a", """{"ask":"Do the item."}""", T0, conversation, Ct);
        Assert.True(opened.Exchange is not null, $"the item could not be started: {opened.Admission.Rejection}");
    }

    [Fact]
    public async Task Open_work_started_while_the_revision_warning_is_shown_is_not_removed_unwarned()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        var person = (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => PersonSeat.Is(a.Name)).Name;
        await StartItemAsync(person, Billing, "rm1-1");

        await ProposeAsync("Billing export", target: "billing export");

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);
        Accept(driving, Proposing.Title, "Revise the roadmap 'Billing export'");
        await shell.Settled;

        var warning = (ChoiceDialog)driving.Find(Proposing.Title);
        Assert.Contains("item 1", warning.Described, StringComparison.Ordinal);
        Assert.DoesNotContain("item 2", warning.Described, StringComparison.Ordinal);

        // While the person reads the warning, item 2 is started elsewhere.
        await StartItemAsync(person, Orders, "rm1-2");

        Accept(driving, Proposing.Title, Proposing.RemoveAndConfirm);
        await shell.Settled;

        var revision = (await Client.RoadmapsAsync(Ct)).Single().Revision;
        Assert.True(
            revision == 1,
            $"the revision was confirmed, removing item 2's open work, which the warning never named. Said: {shell.Said}");
    }

    [Fact]
    public async Task Open_work_started_while_an_edits_removal_warning_is_shown_is_not_removed_unwarned()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        var person = (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => PersonSeat.Is(a.Name)).Name;
        await StartItemAsync(person, Orders, "rm1-2");

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Edit();
        await shell.Settled;

        var form = (FormDialog)driving.Find(Revising.Title);
        form.Fill(0, "title: Billing export\n1. Something else entirely | for: billing@test");
        form.Finish(taken: true);
        await shell.Settled;

        var asking = (ChoiceDialog)driving.Find(Revising.Title);
        Assert.Contains("item 2", asking.Described, StringComparison.Ordinal);
        Assert.DoesNotContain("item 1", asking.Described, StringComparison.Ordinal);

        // While the person reads the warning, item 1 is started elsewhere.
        await StartItemAsync(person, Billing, "rm1-1");

        driving.Row(asking.Choices, Revising.RemoveAnyway);
        driving.Enter();
        await shell.Settled;

        var id = Assert.Single(await Client.RoadmapsAsync(Ct)).Id;
        var item1 = (await Client.RoadmapAsync(id, Ct))!.Items.Single(i => i.Number == 1);
        Assert.False(item1.Removed, $"item 1's open work was removed, though the warning never named it. Said: {shell.Said}");
    }

    [Fact]
    public async Task Edit_then_confirm_when_the_shown_roadmap_was_renamed_confirms_nothing()
    {
        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);
        var proposal = await ProposeAsync("Billing export v2", target: "billing export");

        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        driving.Press(Key.E.WithAlt);
        await shell.Settled;

        var roadmap = (await Repository.ListAsync(Ct)).Single();
        Assert.True((await Repository.EditAsync(
            roadmap.Id,
            roadmap.Revision,
            new RoadmapContent("Exports", [new RoadmapItemDraft(1, "Add CSV export", Billing, [])]),
            T0,
            cancellationToken: Ct)).Done);

        var form = (FormDialog)driving.Find(Proposing.Title);
        form.Fill(0, "title: Billing export v3\n1. Add a CSV export endpoint | for: billing@test");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Single(await Client.RoadmapsAsync(Ct));
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(proposal, Ct))!.State);
        Assert.StartsWith("The roadmap 'Billing export' is no longer called that", shell.Said, StringComparison.Ordinal);
    }
}
