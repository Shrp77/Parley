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
/// <b>An enrolled agent must be registered.</b> A roster row with no <c>@machine</c> suffix which means nothing has
/// ever attached under that name — and a name nothing has registered is unroutable under §14, so
/// <em>every</em> message type is refused. Not a quiet agent: one nothing can address at all.
/// </para>
/// <para>
/// So <c>enroll apply</c> must start what it writes, and not say *"Restart your editor in each
/// repository"* — a step that would exist only to compensate. The terminal's enrolment does the same.
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
    public async Task A_node_that_has_not_read_the_new_catalogue_yet_is_asked_again()
    {
        // **Enrolling four agents must not leave none registered.** Enrolment writes the catalogue and asks
        // for the starts in the same breath, and the node re-reads its catalogue on a poll -- so the first
        // ask can reach a node that does not know the role yet, which answers "not something I can
        // start", and that must not be taken as final. Must fail against a start that asks once.
        var token = TestContext.Current.CancellationToken;

        var before = await NodeStub.JoinAsync(_broker.Address, ["billing"], token);
        var heard = new List<string>();

        var starting = Registering.StartAsync(
            _broker.Address, Orders, TimeProvider.System, token, said => { lock (heard) { heard.Add(said); } });

        // The node's poll catching up: it leaves and comes back knowing the new role.
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        await before.DisposeAsync();

        // The broker hears the first stream close a moment after it is dropped, and refuses a second node for the
        // machine until it has (one node per machine, SPEC.md §4), as it would refuse a node that came back too
        // quickly; the stand-in node retries as a real one does (CI, Linux, 0.47.40).
        NodeStub? joined = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (joined is null)
        {
            try
            {
                joined = await NodeStub.JoinAsync(_broker.Address, ["orders"], token);
            }
            catch (Grpc.Core.RpcException refused) when (refused.StatusCode == Grpc.Core.StatusCode.AlreadyExists
                                                         && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            }
        }

        await using var after = joined;

        var command = await after.NextAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
        Assert.Equal(Orders.Value, command.Start.Agent);

        await using var attached = await AttachedStub.AttachAsync(_broker.Address, Orders, token);

        var outcome = await starting;

        Assert.True(outcome.Registered, outcome.Says);

        // And it said so while it waited, which is what a person watching the screen has to go on.
        Assert.Contains(Registering.WaitingForNode, heard);
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
    public async Task A_broker_that_answers_is_not_a_reason_to_refuse()
    {
        var token = TestContext.Current.CancellationToken;

        Assert.Null(await Registering.UnreachableAsync(_broker.Address, "alpha", token));
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_is_a_reason_to_refuse()
    {
        // The rule: adding an agent has to leave it registered, registering is the agent attaching,
        // and attaching needs a broker. So a run that cannot reach one cannot finish the job, and
        // writing the files anyway leaves the state the whole feature exists to remove.
        var token = TestContext.Current.CancellationToken;

        var why = await Registering.UnreachableAsync("http://127.0.0.1:1", "alpha", token);

        Assert.NotNull(why);
        Assert.Contains("alpha", why, StringComparison.Ordinal);

        // Actionable rather than merely true. Somebody reading this has to know what to do next,
        // and the order it names is the one this change inverted.
        Assert.Contains("Start it first", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_that_is_not_an_address_is_reported_rather_than_thrown()
    {
        // **A fixture can record its endpoint** as
        // `127.0.0.1:51707`, with no scheme — which `GrpcChannel.ForAddress` refuses by throwing.
        // This runs among the manifest's other refusals, and one of them throwing would take the
        // whole run down where every other says what is wrong with it.
        var token = TestContext.Current.CancellationToken;

        var why = await Registering.UnreachableAsync("127.0.0.1:51707", "alpha", token);

        Assert.NotNull(why);
        Assert.Contains("not an address", why, StringComparison.Ordinal);
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
