using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Whether an agent is still attached, which the attach sequence of SPEC.md §14 acts on.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a lease on a timestamp, and the suite it replaces tested the arithmetic of
/// one: renewal, lapse, and the string comparison the two halves depended on agreeing about.
/// None of that exists now. A connection is open or it is not, so what is left to check is that
/// the two writers agree about which column says so and that retirement still outranks both.
/// </para>
/// <para>
/// The narrowing was the point rather than a loss of coverage: every one of those old tests was
/// pinning a way the estimate could go wrong, and an estimate that is not made cannot.
/// </para>
/// <para>
/// <b>It took one case with it that was not about the estimate at all.</b> Nothing left here
/// attached twice — every test opened one session and closed it — so nothing exercised the fact
/// that <c>connected</c> counts. The reader tested <c>connected = 1</c>, written when the column
/// was a flag, and two windows on one repository therefore read as nobody attached: the third
/// window's attach sequence released work the first was mid-answer on, and neither side was
/// told. It reached CI as an intermittent failure in another assembly, because a race decided
/// whether a departing session's decrement had landed yet.
/// </para>
/// <para>
/// So the cases below now count up and down as well as on and off. The lesson is narrower than
/// "narrowing is bad": a suite that drops a case must drop it for the reason it was written, and
/// these were dropped for being about a timestamp when one of them was about arithmetic.
/// </para>
/// </remarks>
public sealed class LivenessLeaseTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentRepository _agents;

    public LivenessLeaseTests()
    {
        _agents = new AgentRepository(_connection);

        _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Full, null, null, null, false,
                AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private Task<bool> Attached() =>
        _agents.IsAttachedAsync(Orders, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Two_sessions_are_still_an_attachment()
    {
        // The case this suite did not have, and the whole of the bug. `connected` counts, and
        // the predicate read `connected = 1` -- written when the column was a flag -- so two
        // windows on one repository read as nobody at all, and the attach sequence then released
        // work the first was mid-answer on.
        //
        // Its own remarks named counting as the fix for exactly this and did not notice it had
        // already landed on the writing side.
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        Assert.True(await Attached());
    }

    [Fact]
    public async Task An_attachment_survives_every_session_but_the_last_going()
    {
        // Counting down as well as up, and the property the attach sequence actually depends on:
        // the answer stays true until the last connection closes, and turns false exactly then.
        for (var session = 0; session < 3; session++)
        {
            await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        }

        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        Assert.True(await Attached());

        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        Assert.True(await Attached());

        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        Assert.False(await Attached());
    }

    [Fact]
    public async Task A_detach_with_no_attach_cannot_drive_the_count_below_nothing()
    {
        // The floor MarkOfflineAsync writes, checked rather than trusted. A negative count would
        // make an agent that can never read as attached again however many sessions open -- and
        // the failure would be the opposite of this suite's headline bug and just as silent.
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);

        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        Assert.True(await Attached());
    }

    [Fact]
    public async Task A_registration_on_its_own_is_not_an_attachment()
    {
        // The state most agents are in most of the time: configured, and nobody running them.
        Assert.False(await Attached());
    }

    [Fact]
    public async Task Attaching_and_detaching_are_the_two_writers()
    {
        // The whole contract. One method sets it, one clears it, and nothing else writes the
        // column -- which is what makes the answer an observation rather than an interpretation.
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        Assert.True(await Attached());

        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        Assert.False(await Attached());
    }

    [Fact]
    public async Task An_attached_agent_is_running_without_a_node_saying_anything()
    {
        // Its own connection is the evidence, and it is the most direct evidence there is. No
        // node has joined in this test and none needs to: SPEC.md §4 has an agent speak to the
        // broker through its own adapter rather than through its machine's supervisor, so an
        // agent that is talking to us is reachable whether or not anything else on that box is.
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        var record = (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == Orders);

        Assert.Equal(AgentPresence.Running, record.Presence);
    }

    [Fact]
    public async Task Detaching_leaves_the_machine_reachable()
    {
        // A person closing one repository has not taken the machine off the air, and the node is
        // the only thing entitled to say it has. An agent here becomes startable rather than
        // stranded, which is the difference between "nobody has this open" and "there is nothing
        // left that could open it".
        //
        // The node is joined explicitly, because that is now the only thing that marks a machine
        // reachable. Attaching an agent used to do it as a stopgap for there being no node
        // process, which meant a deployment with no supervisor anywhere reported every idle
        // agent as startable when nothing could start one.
        await new NodeRepository(_connection).JoinAsync(
            new NodeRecord("test", "test-os", null, null, null, Connected: true, T0),
            T0,
            TestContext.Current.CancellationToken);

        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);

        var record = (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == Orders);

        Assert.Equal(AgentPresence.Startable, record.Presence);
    }

    [Fact]
    public async Task A_retired_agent_cannot_attach()
    {
        // Retirement is the one judgement that outranks an observation, and the check lives here
        // rather than only at admission: an adapter whose agent was retired while it was
        // starting must not write itself back into service.
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal(0, await _agents.MarkLiveAsync(
            Orders, null, T0, TestContext.Current.CancellationToken));

        Assert.False(await Attached());
    }

    [Fact]
    public async Task Attaching_reports_only_its_own_row()
    {
        // The return value means "did this agent's row change", which is how a caller learns its
        // agent was retired out from under it. Attaching also marks the node up, and folding
        // that into the count would answer a different question with the same number.
        Assert.Equal(1, await _agents.MarkLiveAsync(
            Orders, null, T0, TestContext.Current.CancellationToken));
    }
}
