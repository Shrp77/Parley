using Parley.Client.Views;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That a frozen row says any start of the broker lifts an ordinary freeze, not a logon alone, and that a kept freeze says
/// it outlasts one (SPEC.md §14).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a: both phrases were reworded after an install or a crash was seen to lift a freeze nobody
/// had logged on to lift (lens 3 run B on 6257cf80), and every test passed with the old words put back, because each
/// reads the constant rather than what it says.
/// </remarks>
[Unit]
public sealed class FrozenStandingSaysAnyStartLiftsItTests
{
    [Fact]
    public void An_ordinary_freeze_says_the_brokers_next_start_lifts_it()
    {
        Assert.Contains("the broker next starts", Standing.Frozen, StringComparison.Ordinal);
        Assert.DoesNotContain("log on", Standing.Frozen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kept_freeze_says_it_outlasts_the_brokers_start()
    {
        Assert.Contains("stays frozen when the broker starts again", Standing.FrozenKept, StringComparison.Ordinal);
        Assert.DoesNotContain("logon", Standing.FrozenKept, StringComparison.Ordinal);
    }
}
