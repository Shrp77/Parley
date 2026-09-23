using Grpc.Net.Client;
using Parley.Client;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the queue is still one queue when two threads reach it at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.13. Run 1.5 established that queued work runs in the order it
/// was asked for and that <c>Settled</c> is the whole chain rather than the last thing added. Both
/// were asked one caller at a time, which is the easy half: the chain is a field read, extended,
/// and written back, and every guarantee this surface makes about ordering rests on that being one
/// step rather than three.
/// </para>
/// <para>
/// It matters beyond a hypothetical because <c>Settled</c> is what makes every driven journey in
/// this suite deterministic. Work chained off a stale read still runs, so nothing is lost — but it
/// runs beside the work that displaced it rather than after it, and the task a test awaits no
/// longer covers it. A suite that green-lights a race is worse than one that has a race.
/// </para>
/// </remarks>
public sealed class ContentionTests
{
    private static Shell Open() =>
        new(new BrokerDirectory([], _ => GrpcChannel.ForAddress("http://127.0.0.1:1")));

    [Fact]
    public async Task Work_queued_from_many_threads_at_once_all_runs()
    {
        using var shell = Open();

        var ran = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(
            () => shell.Queued(_ =>
            {
                Interlocked.Increment(ref ran);

                return Task.CompletedTask;
            }),
            TestContext.Current.CancellationToken)));

        await shell.Settled;

        Assert.Equal(64, ran);
    }

    [Fact]
    public async Task No_two_pieces_of_queued_work_ever_overlap()
    {
        // The guarantee the queue exists for. Two reads running at once is how one project's agents
        // end up drawn under another's name, and it is not a thing a single-threaded probe can see.
        using var shell = Open();

        var inside = 0;
        var overlapped = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(
            () => shell.Queued(async _ =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    Interlocked.Increment(ref overlapped);
                }

                await Task.Yield();

                Interlocked.Decrement(ref inside);
            }),
            TestContext.Current.CancellationToken)));

        await shell.Settled;

        Assert.Equal(0, overlapped);
    }

    [Fact]
    public async Task Settled_covers_the_work_queued_by_every_thread()
    {
        // The half a lost link hides: work chained off a stale read still runs, so counting what
        // ran eventually proves nothing. What must hold is that it has run by the time the task a
        // keystroke handed back has completed.
        using var shell = Open();

        var ran = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(
            () => shell.Queued(async _ =>
            {
                await Task.Delay(1, TestContext.Current.CancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref ran);
            }),
            TestContext.Current.CancellationToken)));

        await shell.Settled;

        Assert.Equal(64, ran);
    }

    [Fact]
    public async Task A_thread_that_queues_a_failure_does_not_break_the_chain_for_the_others()
    {
        // Run 1.5 asked this of one caller. The interesting version is contended: a faulted link
        // that displaced somebody else's would strand it outside the chain rather than after it.
        using var shell = Open();

        var ran = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(at => Task.Run(
            () =>
            {
                if (at % 4 == 0)
                {
                    shell.Failing(new InvalidOperationException("probe"));
                }

                shell.Queued(_ =>
                {
                    Interlocked.Increment(ref ran);

                    return Task.CompletedTask;
                });
            },
            TestContext.Current.CancellationToken)));

        await shell.Settled;

        Assert.Equal(32, ran);
        Assert.Equal("probe", shell.Said);
    }
}
