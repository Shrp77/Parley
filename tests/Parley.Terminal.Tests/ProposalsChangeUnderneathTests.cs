using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal's proposal and plan views say what is so when proposals and roadmaps change underneath them.
/// </summary>
/// <remarks>
/// <b>Found by review, round 18.</b> The proposal view was never told which live roadmap a revision's hint names, so it
/// said there was none by that name, and a person could decline a revision of one that exists on its word. The other
/// cases held: a proposal decided elsewhere is not decided again, a supersede of many leaves none counted, and a
/// double click on Edit opens one form.
/// </remarks>
public sealed class ProposalsChangeUnderneathTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ProposalsChangeUnderneathTests()
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

    private static async Task OpenFirstProposalAsync(Shell shell)
    {
        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
    }

    [Fact]
    public async Task A_revision_proposal_whose_roadmap_exists_is_not_shown_as_naming_none()
    {
        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);
        await ProposeAsync("Billing export v2", target: "billing export");

        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await OpenFirstProposalAsync(shell);

        var view = (ProposalView)driving.Find(Proposing.Title);

        Assert.DoesNotContain("there is none by that name", view.Header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_declined_elsewhere_while_its_view_is_open_is_not_confirmed_or_told_twice()
    {
        var id = await ProposeAsync();

        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await OpenFirstProposalAsync(shell);
        Assert.IsType<ProposalView>(driving.Find(Proposing.Title));

        // Another surface declines it while the person reads.
        Assert.True((await Repository.DeclineAsync(id, T0, cancellationToken: Ct)).Done);

        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        Assert.Empty(await Client.RoadmapsAsync(Ct));
        Assert.DoesNotContain("Confirmed", shell.Said, StringComparison.Ordinal);
        Assert.Single(await Repository.DrainNoticesAsync(Orders, 10, T0, cancellationToken: Ct));
        Assert.IsType<Shell>(driving.App.TopRunnableView);
        Assert.Equal(0, shell.Proposals ?? 0);
    }

    [Fact]
    public async Task A_proposal_confirmed_elsewhere_while_its_view_is_open_is_not_said_declined()
    {
        var id = await ProposeAsync();

        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await OpenFirstProposalAsync(shell);

        var whole = await Repository.GetProposalAsync(id, Ct);
        Assert.True((await Repository.ConfirmAsync(id, whole!.Content, null, T0, cancellationToken: Ct)).Done);

        driving.Press(Key.D.WithAlt);
        await shell.Settled;

        var asking = (ChoiceDialog)driving.Find(Proposing.DeclineTitle);
        driving.Row(asking.Choices, Proposing.Decline);
        asking.Choices.InvokeCommand(Command.Accept);
        await shell.Settled;

        Assert.DoesNotContain("Declined", shell.Said, StringComparison.Ordinal);
        Assert.Single(await Client.RoadmapsAsync(Ct));
        Assert.Equal(ProposalState.Confirmed, (await Repository.GetProposalAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task After_a_revision_supersedes_many_the_count_is_none()
    {
        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        for (var n = 0; n < 40; n++)
        {
            await ProposeAsync("Billing export", target: "billing export");
        }

        using var driving = Driving.Open(width: 200, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        Assert.Equal(40, shell.Proposals);

        await OpenFirstProposalAsync(shell);
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Proposing.Title);
        driving.Row(choosing.Choices, "Revise the roadmap 'Billing export'");
        choosing.Choices.InvokeCommand(Command.Accept);
        await shell.Settled;

        Assert.Equal(0, await Client.ProposalCountAsync(ProposalState.Waiting, Ct));
        Assert.True(shell.Proposals is null or 0, $"status line still counts {shell.Proposals}; said: {shell.Said}");
        Assert.Empty(shell.Roadmaps.Proposals);
    }

    /// <summary>One double click as a terminal sends it: a click, then a double click, each hit-tested against what is on top.</summary>
    private static void DoubleClickAt(Driving.Session driving, System.Drawing.Point at)
    {
        foreach (var flags in new[] { MouseFlags.LeftButtonClicked, MouseFlags.LeftButtonDoubleClicked })
        {
            var top = driving.App.TopRunnableView!;
            var under = top.GetViewsUnderLocation(in at, ViewportSettingsFlags.None);
            var target = under.LastOrDefault(v => v is not null && v is not AdornmentView && v.GetType().Name != "ShadowView") ?? top;

            target.NewMouseEvent(new Mouse
            {
                Flags = flags,
                ScreenPosition = at,
                Position = target.ScreenToViewport(in at),
                View = target,
            });
        }
    }

    private static System.Drawing.Point Middle(View view)
    {
        var frame = view.FrameToScreen();

        return new System.Drawing.Point(frame.X + (frame.Width / 2), frame.Y + (frame.Height / 2));
    }

    [Fact]
    public async Task A_double_click_on_Edit_roadmap_opens_one_form_and_saves_nothing()
    {
        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        var edit = view.SubViews.OfType<Button>().Single(b => b.Title == PlanView.EditLabel);

        DoubleClickAt(driving, Middle(edit));
        await shell.Settled;

        Assert.IsType<FormDialog>(driving.App.TopRunnableView);
        Assert.Equal(1, (await Client.RoadmapsAsync(Ct)).Single().Revision);
    }
}
