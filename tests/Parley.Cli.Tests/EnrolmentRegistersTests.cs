using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Setup;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That enrolment leaves the agent registered, and is honest when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin was met by the owner and traced from a symptom that looked like
/// nothing.</b> Most rows in the roster carried no <c>@machine</c> suffix, which means nothing has
/// ever attached under that name — and a name nothing has registered is unroutable under §14, so
/// <em>every</em> message type is refused. Not a quiet agent: one nothing can address at all.
/// </para>
/// <para>
/// The cause was that <c>enroll apply</c> wrote two files and started nothing, and said so in its
/// own success message — *"Restart your editor in each repository"* — a step that existed only to
/// compensate. The terminal's enrolment already started what it wrote; the CLI did not.
/// </para>
/// <para>
/// <b>Nothing here may write a registration.</b> §12.3 makes it the adapter's own act on attach, so
/// the only honest way to leave an agent registered is to start it and let it register itself.
/// These drive that through a real socket, with a node that answers, because the interesting part
/// is the sequence and not the arithmetic.
/// </para>
/// </remarks>
public sealed class EnrolmentRegistersTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public EnrolmentRegistersTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static AgentName Orders => AgentName.Create($"orders@{MachineName.Current}");

    [Fact]
    public async Task An_agent_started_by_enrolment_is_seen_to_register()
    {
        // The whole feature in one test: ask, and then look. The node answers the start, the agent
        // attaches under its own identity, and enrolment sees it in the registry.
        var token = TestContext.Current.CancellationToken;

        await using var node = await NodeStub.JoinAsync(_broker.Address, ["orders"], token);

        var starting = Registering.StartAsync(
            _broker.Address, Orders, TimeProvider.System, token);

        // The node's part: it was asked to start, and what a started agent does is attach. That
        // attach is what registers it -- BrokerRuntime.AttachAsync registers an agent it does not
        // know -- and nothing else in this test writes a registry row.
        var command = await node.NextAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders.Value, command.Start.Agent);

        await using var attached = await AttachedStub.AttachAsync(_broker.Address, Orders, token);

        var outcome = await starting;

        Assert.True(outcome.Registered, outcome.Says);
        Assert.Contains("registered", outcome.Says, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_machine_whose_node_cannot_start_the_role_is_told_plainly()
    {
        // Not a failure, and the wording has to carry that: the two files are written and correct,
        // and the agent is startable the moment somebody opens its repository.
        var token = TestContext.Current.CancellationToken;

        await using var node = await NodeStub.JoinAsync(_broker.Address, ["billing"], token);

        var outcome = await Registering.StartAsync(
            _broker.Address, Orders, TimeProvider.System, token);

        Assert.False(outcome.Registered);
        Assert.Contains("written", outcome.Says, StringComparison.OrdinalIgnoreCase);

        foreach (var wrong in new[] { "failed", "error", "could not write" })
        {
            Assert.DoesNotContain(wrong, outcome.Says, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_broker_that_is_not_there_is_reported_rather_than_thrown()
    {
        // The two files are already written when this runs, so an unreachable broker must not take
        // the whole run down with it.
        var token = TestContext.Current.CancellationToken;

        var outcome = await Registering.StartAsync(
            "http://127.0.0.1:1", Orders, TimeProvider.System, token);

        Assert.False(outcome.Registered);
        Assert.Contains("written", outcome.Says, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Running_out_of_patience_says_it_does_not_know()
    {
        // **008's lesson, applied before the same mistake was available to make.** That feature
        // exists because a broker stopped waiting before a node had finished and then recorded the
        // opposite of what happened -- it waited five minutes, the node waited twenty, and the real
        // answer was refused on arrival. A run reporting failure for an agent that registered a
        // second later would send somebody to fix what was already working.
        var token = TestContext.Current.CancellationToken;
        var clock = new StubClock(DateTimeOffset.UtcNow);

        await using var node = await NodeStub.JoinAsync(_broker.Address, ["orders"], token);

        var starting = Registering.StartAsync(_broker.Address, Orders, clock, token);

        _ = await node.NextAsync(TimeSpan.FromSeconds(5));

        // Nothing attaches. The clock runs past the ceiling instead — and it is pushed repeatedly
        // rather than once, because a single advance races the moment the wait captures its own
        // deadline: landing a fraction early sets the deadline from the advanced instant and the
        // wait then never expires. That is a fault in the test rather than in the code, and it hung
        // for ten minutes before it was found.
        var giveUp = DateTime.UtcNow.AddSeconds(30);

        while (!starting.IsCompleted && DateTime.UtcNow < giveUp)
        {
            clock.Advance(Registering.Patience);

            await Task.Delay(50, token);
        }

        Assert.True(starting.IsCompleted, "the wait never gave up, however far the clock was pushed");

        var outcome = await starting;

        Assert.False(outcome.Registered);
        Assert.Equal(Registering.NotSeen, outcome.Says);
    }

    [Fact]
    public void The_wait_running_out_never_reads_as_a_failure()
    {
        // Asserted against the constant rather than against a run, so a rewording is caught even
        // when no test happens to reach the timeout.
        Assert.Contains("had not registered", Registering.NotSeen, StringComparison.Ordinal);

        foreach (var wrong in new[] { "failed", "did not register.", "could not" })
        {
            Assert.DoesNotContain(wrong, Registering.NotSeen, StringComparison.OrdinalIgnoreCase);
        }
    }
}
