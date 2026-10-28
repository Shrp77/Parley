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
/// That an agent's long roadmap title never pushes what a choice does, or the proposal's own title, off the screen.
/// </summary>
/// <remarks>
/// <b>Found by review, round 56.</b> Roadmap text has no bound, and a choice dialog cannot scroll and cuts its tail: the
/// decline door put the agent's title first, so a long one hid what declining does; the revise choice quoted the
/// matched roadmap's title whole, hiding that a revision replaces every item; and a long revise hint in the proposal's
/// heading hid the title a confirm records. Each sentence now quotes the agent's words short.
/// </remarks>
public sealed class ALongAgentTitleHidesNothingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Long = string.Concat(Enumerable.Repeat("Quarterly billing export ", 120)).Trim();

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ALongAgentTitleHidesNothingTests()
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_connection);

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task The_decline_door_says_what_declining_does_whatever_the_title(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        await ProposeAsync(Long);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        driving.Press(Key.D.WithAlt);
        await shell.Settled;

        _ = (ChoiceDialog)driving.Find(Proposing.DeclineTitle);
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains("Proposed by orders@test", screen, StringComparison.Ordinal);
        Assert.Contains("Declining keeps no roadmap", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task The_revise_choice_says_a_revision_replaces_every_item_whatever_the_title(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync(Long), Ct);
        Assert.True((await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct)).Done);
        await ProposeAsync("Short", target: Long);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        _ = (ChoiceDialog)driving.Find(Proposing.Title);
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains("replaces every item", screen.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Make it a new roadmap", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public void A_long_revise_hint_leaves_the_proposals_title_in_view(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        var content = new RoadmapContent(
            "Rotate every production key",
            [new RoadmapItemDraft(null, "Delete the export bucket", Billing, [])]);
        var proposal = new RoadmapProposal(1, Orders, null, Long, content, 10, ProposalState.Waiting, T0, null);

        Prompting.Open(driving.App, new ProposalView(proposal));
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains(ProposalView.ConfirmLabel, screen, StringComparison.Ordinal);
        Assert.Contains("Rotate every production key", screen, StringComparison.Ordinal);
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<long> ProposeAsync(string title, string? target = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            target,
            new RoadmapContent(
                title,
                [
                    new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
                ]),
            T0,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    private static async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };
        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        return shell;
    }
}
