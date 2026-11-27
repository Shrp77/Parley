using Xunit;

namespace Parley.Server.Tests;

/// <summary>That a session's stream is told a rewrite started even when it finished before the stream wrote anything (SPEC.md §4.0, §9.1).</summary>
/// <remarks>
/// Found by the Windows nightlies of 2a55820: the queue held one item, newest wins, and a rewrite says it started and then
/// that it finished on one thread. A stream writer slow to take the start, as on a loaded machine, was handed only the
/// finish, and the terminal never said a rewrite was running.
/// </remarks>
[Unit]
public sealed class ARewriteStartIsNotLostToItsFinishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_start_and_its_finish_both_wait_for_a_reader_that_took_neither()
    {
        var queue = AttachedAgent.TidyingQueue();

        Assert.True(queue.Writer.TryWrite(true));
        Assert.True(queue.Writer.TryWrite(false));
        queue.Writer.Complete();

        Assert.Equal([true, false], await Drain(queue.Reader));
    }

    [Fact]
    public async Task Of_several_rewrites_a_reader_missed_it_is_told_the_latest_start_and_its_finish()
    {
        var queue = AttachedAgent.TidyingQueue();

        foreach (var underway in new[] { true, false, true, false, true, false })
        {
            Assert.True(queue.Writer.TryWrite(underway));
        }

        queue.Writer.Complete();

        Assert.Equal([true, false], await Drain(queue.Reader));
    }

    [Fact]
    public async Task A_rewrite_still_running_is_the_last_thing_a_late_reader_hears()
    {
        var queue = AttachedAgent.TidyingQueue();

        foreach (var underway in new[] { true, false, true })
        {
            Assert.True(queue.Writer.TryWrite(underway));
        }

        queue.Writer.Complete();

        Assert.Equal([false, true], await Drain(queue.Reader));
    }

    private static async Task<List<bool>> Drain(System.Threading.Channels.ChannelReader<bool> reader)
    {
        var read = new List<bool>();

        await foreach (var underway in reader.ReadAllAsync(Ct))
        {
            read.Add(underway);
        }

        return read;
    }
}
