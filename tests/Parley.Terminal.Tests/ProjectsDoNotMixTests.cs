using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That two projects on one terminal never mix: the box and the lists are each one project's.
/// </summary>
/// <remarks>
/// Conversation identifiers are per project, and <c>rm1-1</c> exists in every project with a roadmap, so anything keyed by an
/// identifier alone, or drawn from a read without asking which project is on screen, can cross between them.
/// </remarks>
public sealed class ProjectsDoNotMixTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly ProjectName AlphaName = ProjectName.Create("alpha");
    private static readonly ProjectName BetaName = ProjectName.Create("beta");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _alpha;
    private readonly SqliteConnection _beta;
    private readonly LoopbackOperator _alphaBroker;
    private readonly LoopbackOperator _betaBroker;

    public ProjectsDoNotMixTests()
    {
        _alpha = ParleyDatabase.OpenInMemory();
        _beta = ParleyDatabase.OpenInMemory();

        foreach (var connection in new[] { _alpha, _beta })
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                    TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }
        }

        _alphaBroker = LoopbackOperator.Start(_alpha);
        _betaBroker = LoopbackOperator.Start(_beta);
    }

    public async ValueTask DisposeAsync()
    {
        await _alphaBroker.DisposeAsync();
        await _betaBroker.DisposeAsync();

        _alpha.Dispose();
        _beta.Dispose();
    }

    private static async Task SeedAsync(SqliteConnection connection, AgentName to, string conversation, string said)
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Broker.Broker(connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, ct);

        var sent = await broker.RequestAsync(
            Orders, to, "model-a", $$"""{"ask":"{{said}}"}""", T0.AddMinutes(1), conversation, ct);

        Assert.True(sent.Exchange is not null, $"seed refused: {sent.Admission.Rejection}");
    }

    [Fact]
    public async Task Words_written_in_alphas_c1_do_not_follow_into_betas_c1()
    {
        // Must fail against keying the box by the conversation id alone, which carried alpha's words into beta's c-1.
        var ct = TestContext.Current.CancellationToken;

        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        shell.FollowEvery = null;
        await shell.RefreshAsync(ct);
        await shell.Settled;

        // Every project's seat is taken on refresh; both conversations are an agent asking the person.
        await SeedAsync(_alpha, seats.Known("alpha")!.Name, "c-1", "alpha question");
        await SeedAsync(_beta, seats.Known("beta")!.Name, "c-1", "beta question");

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(AlphaName).Into("c-1"));
        await shell.Settled;
        Assert.Contains("alpha question", shell.Transcript.Written, StringComparison.Ordinal);

        shell.Line.Written = "ship the alpha totals tonight";

        // F3, then Escape twice: none of them asks.
        await shell.GoAsync(ViewPlace.Configure(AlphaName));
        await shell.Settled;
        await shell.BackAsync();
        await shell.Settled;
        await shell.BackAsync();
        await shell.Settled;

        await shell.GoAsync(ViewPlace.Into(BetaName));
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(BetaName).Into("c-1"));
        await shell.Settled;
        Assert.Contains("beta question", shell.Transcript.Written, StringComparison.Ordinal);

        var carried = shell.Line.Written;

        shell.Line.Accept();
        await shell.Settled;

        var export = await new BrokerClient(_betaBroker.Operators).ExportAsync("c-1", ct);
        var leaked = export!.Messages.Any(m => m.PayloadJson.Contains("alpha totals", StringComparison.Ordinal));

        Assert.False(
            leaked || carried.Length > 0,
            $"box on arriving at beta/c-1 held '{carried}'; sent to beta's broker: {leaked}; said: {shell.Said}");
    }

    [Fact]
    public async Task A_late_read_of_alpha_does_not_draw_alphas_lists_in_betas_project_view()
    {
        // Enter on a project reads it off the queue. The person leaves and enters another project before it returns.
        // Must fail against the late read drawing alpha's conversations in beta's view.
        var ct = TestContext.Current.CancellationToken;

        await SeedAsync(_alpha, Billing, "only-alpha", "alpha question");
        await SeedAsync(_beta, Billing, "only-beta", "beta question");

        var gate = new HoldOne("/Roadmaps");
        using var hold = gate;
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _alphaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = gate })
                : GrpcChannel.ForAddress(address));
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Row(shell.Projects.List, "alpha");
        await shell.Settled;

        gate.Arm();
        _ = shell.OpenAsync(); // what Enter on the projects view does
        Assert.True(gate.Holding.Wait(TimeSpan.FromSeconds(10), ct), "alpha's read never reached the broker");

        await shell.BackAsync(); // Escape
        await shell.Settled;

        driving.Row(shell.Projects.List, "beta");
        await shell.Settled;
        await shell.OpenAsync(); // Enter on beta
        await shell.Settled;

        var before = string.Join(" | ", shell.Conversations.List.Source!.ToList().Cast<object>());
        Assert.Contains("only-beta", before, StringComparison.Ordinal);

        gate.Release();
        await Task.Delay(1500, ct);
        await shell.Settled;

        var after = string.Join(" | ", shell.Conversations.List.Source!.ToList().Cast<object>());

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.True(
            after.Contains("only-beta", StringComparison.Ordinal) && !after.Contains("only-alpha", StringComparison.Ordinal),
            $"beta's project view lists: {after}");
    }
}
