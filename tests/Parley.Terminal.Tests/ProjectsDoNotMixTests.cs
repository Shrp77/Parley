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

    /// <summary>Fails every call to one path, as a broker that does not answer it.</summary>
    private sealed class FailPath(string path) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal)
                ? throw new HttpRequestException("not answering")
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Alphas_roadmaps_are_not_listed_in_betas_view_when_betas_cannot_be_read()
    {
        // "Unknown leaves what was shown" is for the same project. Must fail against beta's view keeping alpha's roadmaps
        // after beta's read failed, where choosing one would act on alpha's id through beta's broker.
        var ct = TestContext.Current.CancellationToken;

        var repository = new RoadmapRepository(_alpha);
        var (proposal, problem) = await repository.ProposeAsync(
            Orders, null, null, new RoadmapContent("Alpha only plan", [new RoadmapItemDraft(null, "Ship alpha", Billing, [])]), T0,
            cancellationToken: ct);
        Assert.Null(problem);
        var waiting = await repository.GetProposalAsync(proposal!.Value, ct);
        await repository.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, cancellationToken: ct);

        using var failing = new FailPath("/Roadmaps");
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _betaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = failing })
                : GrpcChannel.ForAddress(address));
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;
        Assert.Contains(shell.Plans.Roadmaps, r => r.Title.Contains("Alpha only plan", StringComparison.Ordinal));

        await shell.GoAsync(ViewPlace.Into(BetaName));
        await shell.Settled;

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.DoesNotContain(shell.Plans.Roadmaps, r => r.Title.Contains("Alpha only plan", StringComparison.Ordinal));
        Assert.DoesNotContain("Alpha only plan", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_late_mail_poll_of_alpha_does_not_draw_alphas_roadmaps_in_betas_view()
    {
        // The mail poll reads the roadmaps of the project in view. Must fail against it drawing them after the person has
        // moved to another project, where beta's own read failing left nothing to correct the pane.
        var ct = TestContext.Current.CancellationToken;

        var repository = new RoadmapRepository(_alpha);
        var (proposal, problem) = await repository.ProposeAsync(
            Orders, null, null, new RoadmapContent("Alpha only plan", [new RoadmapItemDraft(null, "Ship alpha", Billing, [])]), T0,
            cancellationToken: ct);
        Assert.Null(problem);
        var waiting = await repository.GetProposalAsync(proposal!.Value, ct);
        await repository.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, cancellationToken: ct);

        var gate = new HoldOne("/Roadmaps");
        using var hold = gate;
        using var failing = new FailPath("/Roadmaps");
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _alphaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = gate })
                : GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = failing }));
        using var seats = new SeatBook(directory.AgentsFor);
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = TimeSpan.FromMilliseconds(100), FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;
        Assert.Contains(shell.Plans.Roadmaps, r => r.Title.Contains("Alpha only plan", StringComparison.Ordinal));

        gate.Arm();
        Assert.True(gate.Holding.Wait(TimeSpan.FromSeconds(10), ct), "the poll never read alpha's roadmaps");

        await shell.GoAsync(ViewPlace.Into(BetaName));
        Assert.DoesNotContain(shell.Plans.Roadmaps, r => r.Title.Contains("Alpha only plan", StringComparison.Ordinal));

        gate.Release();
        Assert.True(gate.Answered.Wait(TimeSpan.FromSeconds(10), ct), "the held read never came back");
        await Task.Delay(500, ct);
        await shell.Settled;

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.DoesNotContain(shell.Plans.Roadmaps, r => r.Title.Contains("Alpha only plan", StringComparison.Ordinal));
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
        Assert.True(gate.Answered.Wait(TimeSpan.FromSeconds(10), ct), "the held read never came back");

        // The read lands after its reply: give the rest of it, the draws that follow, a moment to run.
        await Task.Delay(500, ct);
        await shell.Settled;

        var after = string.Join(" | ", shell.Conversations.List.Source!.ToList().Cast<object>());

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.True(
            after.Contains("only-beta", StringComparison.Ordinal) && !after.Contains("only-alpha", StringComparison.Ordinal),
            $"beta's project view lists: {after}");
    }

    [Fact]
    public async Task A_late_follow_of_one_projects_conversation_never_draws_in_anothers_of_the_same_id()
    {
        // `c-1` is a conversation in both projects, so the follow loop must compare the project as well as the id: for
        // the transcript, and for the room it redraws after it. Only alpha has inventory, so only alpha's room names it.
        var ct = TestContext.Current.CancellationToken;

        await new AgentRepository(_alpha).UpsertAsync(
            new AgentRecord(AgentName.Create("inventory@test"), "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            ct);

        var gate = new HoldOne("/Export");
        using var hold = gate;
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _alphaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = gate })
                : GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = null };

        driving.Show(shell);
        shell.FollowEvery = TimeSpan.FromMilliseconds(100);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        await SeedAsync(_alpha, seats.Known("alpha")!.Name, "c-1", "alpha question");
        await SeedAsync(_beta, seats.Known("beta")!.Name, "c-1", "beta question");

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(AlphaName).Into("c-1"));
        await shell.Settled;
        Assert.Contains("alpha question", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains(shell.Room.Written, row => row.Contains("inventory", StringComparison.Ordinal));

        // A follow tick of alpha's c-1 is out when the person moves.
        gate.Arm();
        Assert.True(gate.Holding.Wait(TimeSpan.FromSeconds(10), ct), "alpha's follow never reached the broker");

        // Beta's own follow would redraw beta a tick later; off, so what the late read draws stays to be seen.
        shell.FollowEvery = null;

        await shell.GoAsync(ViewPlace.Into(BetaName).Into("c-1"));
        Assert.Contains("beta question", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Room.Written, row => row.Contains("inventory", StringComparison.Ordinal));

        gate.Release();
        Assert.True(gate.Answered.Wait(TimeSpan.FromSeconds(10), ct), "the held read never came back");
        await Task.Delay(500, ct);
        await shell.Settled;

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.True(
            !shell.Transcript.Written.Contains("alpha question", StringComparison.Ordinal),
            $"beta/c-1 shows: {shell.Transcript.Written}");
        Assert.True(
            !shell.Room.Written.Any(row => row.Contains("inventory", StringComparison.Ordinal)),
            $"beta/c-1's room shows: {string.Join(" / ", shell.Room.Written)}");
    }

    [Fact]
    public async Task A_follow_waking_as_another_project_opens_the_same_id_never_draws_there()
    {
        // By the queued route Enter takes. Alpha's follow loop is asleep when the person leaves alpha/c-1 for
        // beta/c-1; it wakes after beta's transcript is shown and before beta's StartFollowing cancels it (FillRoomAsync's
        // reads are between them), finds "c-1" open, and queues a read of alpha's c-1.
        var ct = TestContext.Current.CancellationToken;

        var gate = new HoldOne("/DescribeRegistry");
        using var hold = gate;
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _betaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = gate })
                : GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        await SeedAsync(_alpha, seats.Known("alpha")!.Name, "c-1", "alpha question");
        await SeedAsync(_beta, seats.Known("beta")!.Name, "c-1", "beta question");

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;

        shell.FollowEvery = TimeSpan.FromSeconds(2);
        var opened = System.Diagnostics.Stopwatch.StartNew();
        await shell.GoAsync(ViewPlace.Into(AlphaName).Into("c-1"));
        Assert.Contains("alpha question", shell.Transcript.Written, StringComparison.Ordinal);

        // Escape, and into beta, inside alpha's first follow interval.
        await shell.GoAsync(ViewPlace.Into(BetaName));
        await shell.Settled;
        Assert.True(opened.Elapsed < TimeSpan.FromSeconds(1.5), $"too slow to stay inside the interval: {opened.Elapsed}");

        gate.Arm();
        driving.Row(shell.Conversations.List, "c-1");
        Assert.True(shell.Conversations.OpenSelected());
        Assert.True(gate.Holding.Wait(TimeSpan.FromSeconds(10), ct), "beta's room read never reached the broker");
        Assert.Contains("beta question", shell.Transcript.Written, StringComparison.Ordinal);

        // Alpha's loop wakes now.
        await Task.Delay(TimeSpan.FromSeconds(2.5) - opened.Elapsed, ct);

        gate.Release();
        await shell.Settled;

        Assert.Equal("beta", shell.Place.Project?.Value);
        Assert.True(
            !shell.Transcript.Written.Contains("alpha question", StringComparison.Ordinal),
            $"beta/c-1 shows: {shell.Transcript.Written}");
    }

    [Fact]
    public async Task A_line_in_a_conversation_that_could_not_be_read_never_goes_to_the_last_conversations_recipients()
    {
        // Alpha's c-1 is inventory's question; beta's c-1 is orders'. Beta's export fails, so beta's room is
        // empty and no draft is built for it. Enter in beta's c-1 must not send to whoever alpha's c-1 was addressed to.
        var ct = TestContext.Current.CancellationToken;
        var inventory = AgentName.Create("inventory@test");

        foreach (var connection in new[] { _alpha, _beta })
        {
            await new AgentRepository(connection).UpsertAsync(
                new AgentRecord(inventory, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), ct);
        }

        using var failing = new FailPath("/Export");
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(AlphaName, "unused-a.db", _alphaBroker.Address, null),
                new ProjectSettings(BetaName, "unused-b.db", _betaBroker.Address, null),
            ],
            address => string.Equals(address, _betaBroker.Address, StringComparison.Ordinal)
                ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = failing })
                : GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var driving = Driving.Open(width: 140, height: 40);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        var alphaBroker = new Broker.Broker(_alpha, Parley.Broker.BrokerOptions.Default);
        await alphaBroker.StartAsync(inventory, T0, ct);
        var asked = await alphaBroker.RequestAsync(
            inventory, seats.Known("alpha")!.Name, "model-a", """{"ask":"alpha question"}""", T0.AddMinutes(1), "c-1", ct);
        Assert.True(asked.Exchange is not null, $"seed refused: {asked.Admission.Rejection}");
        await SeedAsync(_beta, seats.Known("beta")!.Name, "c-1", "beta question");

        await shell.GoAsync(ViewPlace.Into(AlphaName));
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(AlphaName).Into("c-1"));
        await shell.Settled;
        Assert.Contains("alpha question", shell.Transcript.Written, StringComparison.Ordinal);
        var alphaDraft = string.Join(",", shell.Drafting?.Recipients.Select(r => r.Value) ?? []);

        await shell.BackAsync();
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(BetaName));
        await shell.Settled;
        await shell.GoAsync(ViewPlace.Into(BetaName).Into("c-1"));
        await shell.Settled;
        Assert.DoesNotContain("beta question", shell.Transcript.Written, StringComparison.Ordinal);

        var draftInBeta = string.Join(",", shell.Drafting?.Recipients.Select(r => r.Value) ?? []);
        shell.Line.Written = "beta words";
        shell.Line.Accept();
        await shell.Settled;

        var export = await new BrokerClient(_betaBroker.Operators).ExportAsync("c-1", ct);
        var reached = export!.Messages
            .Where(m => m.PayloadJson.Contains("beta words", StringComparison.Ordinal))
            .Select(m => m.To.Value)
            .ToList();

        Assert.True(
            reached.Count == 0,
            $"alpha's draft: {alphaDraft}; draft in beta/c-1: {draftInBeta}; beta's c-1 line reached: {string.Join(",", reached)}; said: {shell.Said}");
    }
}
