using Grpc.Net.Client;
using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the one object every page shares survives being shared.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BrokerDirectory"/> is a singleton behind a web server, so every request on every
/// page reaches it at once — and the pages refresh themselves, so concurrency is the normal case
/// rather than the unusual one.
/// </para>
/// <para>
/// The failure a plain dictionary produces here is not a duplicated channel. Two threads growing
/// one at the same instant can leave its buckets in a state where a later read never terminates:
/// a request that hangs forever and a core at a hundred percent, on a console that had been
/// working for weeks. It is also intermittent, which is why it is worth a test rather than a
/// reading.
/// </para>
/// </remarks>
public sealed class ConcurrencyTests
{
    /// <summary>How many threads ask at once.</summary>
    /// <remarks>
    /// Enough to be a real crowd and few enough that every one of them gets a thread: they wait
    /// for each other before asking, so a pool that ran them in batches would never produce the
    /// simultaneous arrival the race needs.
    /// </remarks>
    private const int Callers = 16;

    [Fact]
    public async Task One_channel_is_opened_per_endpoint_however_many_requests_arrive_at_once()
    {
        // Counting the connects is the deterministic half. A factory that ran twice would leave a
        // connected channel nobody holds and nothing disposes -- invisible until a console left
        // open for a week has a socket per refresh.
        var connects = 0;
        var arrived = new CountdownEvent(Callers);

        using var directory = new BrokerDirectory(
            [new ProjectSettings(
                ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:5301", null, null)],
            endpoint =>
            {
                Interlocked.Increment(ref connects);

                // Held open deliberately. Opening a channel is fast enough that a check-then-act
                // race almost never loses on this machine and always will on somebody's -- so the
                // window is widened until the defect is deterministic rather than left to whether
                // the test happened to be unlucky.
                Thread.Sleep(50);

                return GrpcChannel.ForAddress(endpoint);
            });

        var project = directory.Projects[0];

        await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(
            () =>
            {
                // Every caller waits for every other before asking, so they arrive together
                // rather than in whatever order the pool happened to start them.
                arrived.Signal();
                arrived.Wait(TestContext.Current.CancellationToken);

                Assert.NotNull(directory.ClientFor(project));
            },
            TestContext.Current.CancellationToken)));

        arrived.Dispose();

        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task Reading_the_same_project_from_many_threads_terminates()
    {
        // The hang, stated as the property it violates. There is no assertion to make about a
        // corrupted bucket chain other than that the read returns at all -- so this fails by
        // never finishing, which the runner reports as a timeout rather than as a pass.
        using var directory = new BrokerDirectory(
            [new ProjectSettings(
                ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:5301", null, null)],
            GrpcChannel.ForAddress);

        var project = directory.Projects[0];

        await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => Task.Run(
            () => directory.ClientFor(project),
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void A_disposed_directory_refuses_rather_than_handing_out_a_closing_channel()
    {
        // Disposal marks first and closes second, so a request arriving mid-shutdown is told no
        // rather than handed a channel about to be torn out from under it.
        var directory = new BrokerDirectory(
            [new ProjectSettings(
                ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:5301", null, null)],
            GrpcChannel.ForAddress);

        var project = directory.Projects[0];

        directory.ClientFor(project);
        directory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => directory.ClientFor(project));
    }

    [Fact]
    public async Task An_endpoint_that_cannot_be_dialled_takes_down_only_its_own_project()
    {
        // The manifest accepts more than gRPC can dial -- "localhost:5301" with no scheme is the
        // usual way to find that out -- and the throw happens where the channel is opened rather
        // than at startup. Uncontained, one bad row costs the whole page, which is the coupling
        // ReadAsync exists to prevent.
        using var directory = new BrokerDirectory(
            [new ProjectSettings(
                ProjectName.Create("alpha"), "a.db", "localhost:5301", null, null)],
            GrpcChannel.ForAddress);

        var read = await BrokerDirectory.ReadAsync(() =>
            Task.FromResult(directory.ClientFor(directory.Projects[0])));

        Assert.False(read.Reachable);
        Assert.Contains("cannot be reached as written", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_twice_is_not_an_error()
    {
        // The host disposes its singletons at shutdown; a test or a second shutdown path must not
        // turn that into a fault.
        var directory = new BrokerDirectory([], GrpcChannel.ForAddress);

        directory.Dispose();
        directory.Dispose();
    }
}
