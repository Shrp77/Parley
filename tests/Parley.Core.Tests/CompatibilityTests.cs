using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// When two builds of Parley can be used together.
/// </summary>
[Unit]
public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("0.42.1", "0.42.0")]
    [InlineData("0.42.1", "0.42.7")]
    [InlineData("0.42.1-beta", "0.42.0")]
    public void A_patch_difference_is_one_release(string a, string b)
    {
        // Must fail against comparing the whole string: a patch is a fix not yet everywhere, not a mismatch.
        Assert.True(Compatibility.Agree(a, b));
    }

    [Theory]
    [InlineData("0.42.1", "0.41.5")]
    [InlineData("0.42.1", "1.42.1")]
    public void A_minor_or_major_difference_is_not(string a, string b)
    {
        // Must fail against comparing only the first part, or the last. While the major is zero the minor breaks.
        Assert.False(Compatibility.Agree(a, b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("0")]
    public void A_version_that_cannot_be_read_agrees_with_nothing(string? unreadable)
    {
        // A build that cannot say what it is has not been shown to match, and must not read as matching itself.
        Assert.False(Compatibility.Agree(unreadable, unreadable));
        Assert.False(Compatibility.Agree(unreadable, "0.42.1"));
    }

    [Fact]
    public void A_builds_version_is_read_without_its_source_control_suffix()
    {
        var version = Compatibility.VersionOf(typeof(Compatibility).Assembly);

        Assert.DoesNotContain('+', version);
        Assert.NotNull(Compatibility.Family(version));
    }
}
