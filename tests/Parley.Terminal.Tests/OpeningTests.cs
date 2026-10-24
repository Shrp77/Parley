using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That opening conversations one after another shows each of them, by either route.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.11. The whole argument for four regions is that reading one
/// conversation and then the next is an arrow and a key — so the interesting case is not the first
/// open but the third, and not one route but both giving the same answer.
/// </para>
/// <para>
/// Opening the same conversation twice is included because it is what a person does when they
/// think the screen has not updated, and a region that ignored a repeat would prove them right.
/// </para>
/// </remarks>
public sealed class OpeningTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public OpeningTests()
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

    [Fact]
    public async Task Each_conversation_opened_in_turn_shows_its_own_messages()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedAsync("first", "about the totals");
        await SeedAsync("second", "about the stock");
        await SeedAsync("third", "about the tax");

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        foreach (var (id, said) in new[]
                 {
                     ("first", "about the totals"),
                     ("second", "about the stock"),
                     ("third", "about the tax"),
                 })
        {
            driving.Row(shell.Conversations.List, id);

            Assert.True(shell.Conversations.OpenSelected());

            await shell.Settled;

            Assert.Equal(id, shell.Transcript.Open);
            Assert.Contains(said, shell.Transcript.Written, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Opening_the_same_conversation_twice_shows_it_twice()
    {
        // What a person does when they think the screen has not updated. A region that ignored the
        // repeat would prove them right.
        var ct = TestContext.Current.CancellationToken;

        await SeedAsync("only", "about the totals");

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        for (var again = 0; again < 2; again++)
        {
            shell.Transcript.Clear();

            Assert.True(shell.Conversations.OpenSelected());

            await shell.Settled;

            Assert.Equal("only", shell.Transcript.Open);
        }
    }

    [Fact]
    public async Task The_key_and_the_pointer_reach_the_same_transcript()
    {
        // FR-003, asserted as an equivalence rather than as two separate journeys, because what
        // it promises is that neither route can drift from the other.
        var ct = TestContext.Current.CancellationToken;

        await SeedAsync("only", "about the totals");

        using var driving = Driving.Open();
        using var directory = Directory();

        // No timers: the mail poll relays the screen every five seconds, and under a loaded run a tick landing between
        // clearing the transcript and opening the conversation again left it cleared. The routes are what is compared.
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        // **This test passed on Windows and failed on Linux, and Windows was the wrong answer.**
        // It was written for 005's four regions: Tab, Tab, Enter reached the conversations list.
        // After 006 the first Tab does nothing on a one-pane view and Enter opens the project, so
        // neither route reached a transcript -- and the assertion compared the empty state to the
        // empty state and was satisfied. The platforms differed only in which route happened to
        // land first; what they agreed on was that nothing was being checked.
        //
        // So the equivalence is still asserted, and both sides are now required to be a real
        // transcript before they are compared.
        driving.Enter();
        await shell.Settled;

        driving.Tab();
        driving.Enter();
        await shell.Settled;

        var byKey = shell.Transcript.Written;

        Assert.Contains("about the totals", byKey, StringComparison.Ordinal);

        shell.Transcript.Clear();
        Assert.DoesNotContain("about the totals", shell.Transcript.Written, StringComparison.Ordinal);

        // The call both routes end in. `RegionTests` carries why the pointer itself cannot be
        // driven to this point in this harness, and what is checked instead.
        shell.Conversations.OpenSelected();
        await shell.Settled;

        Assert.Equal(byKey, shell.Transcript.Written);
    }

    private async Task SeedAsync(string conversation, string said)
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, ct);
        await broker.StartAsync(Billing, T0, ct);

        await broker.RequestAsync(
            Orders, Billing, "model-a",
            $$"""{"ask":"{{said}}"}""",
            T0.AddMinutes(1), conversation, ct);
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));
}
