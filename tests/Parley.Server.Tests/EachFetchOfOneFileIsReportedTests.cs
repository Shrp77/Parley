using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That every completed stream of one file to one agent can be reported, however many ran at once.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 1 run 63.</b> One agent fetching one file several times at once streams it several times,
/// and each fetch reports. A mark per file let only the first report through, so the others were told the fetch went
/// unrecorded and no notice would follow, though it was recorded.
/// </remarks>
[Integration]
public sealed class EachFetchOfOneFileIsReportedTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    [Fact]
    public void Each_stream_is_reported_once_and_no_more()
    {
        var served = new ServedFetches();
        var hash = ContentHash.Of("net 30"u8);

        served.Served(Orders, "pricing", "notes.md", hash);
        served.Served(Orders, "pricing", "notes.md", hash);
        served.Served(Orders, "pricing", "notes.md", hash);

        Assert.True(served.TryConsume(Orders, "pricing", "notes.md", hash));
        Assert.True(served.TryConsume(Orders, "pricing", "notes.md", hash));
        Assert.True(served.TryConsume(Orders, "pricing", "notes.md", hash));
        Assert.False(served.TryConsume(Orders, "pricing", "notes.md", hash));
    }

    [Fact]
    public void Reports_racing_one_another_take_exactly_as_many_as_were_streamed()
    {
        var served = new ServedFetches();
        var hash = ContentHash.Of("net 30"u8);

        for (var i = 0; i < 64; i++)
        {
            served.Served(Orders, "pricing", "notes.md", hash);
        }

        var taken = 0;

        Parallel.For(0, 200, _ =>
        {
            if (served.TryConsume(Orders, "pricing", "notes.md", hash))
            {
                Interlocked.Increment(ref taken);
            }
        });

        Assert.Equal(64, taken);
    }
}
