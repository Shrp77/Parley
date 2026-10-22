using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Reading what the other Parley programs say they are.
/// </summary>
public sealed class InstalledToolsTests
{
    [Theory]
    [InlineData("parley-terminal 0.42.1, speaking Parley protocol 0.8", "0.42.1")]
    [InlineData("parley-mcp 0.41.5, speaking Parley protocol 0.8\n", "0.41.5")]
    public void The_version_is_read_out_of_what_a_tool_prints(string printed, string expected)
    {
        // Must fail against taking the protocol's two-part number, which also appears on the line.
        Assert.Equal(expected, InstalledTools.Read(printed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("usage: parley-terminal")]
    public void Output_with_no_version_in_it_reads_as_nothing_rather_than_as_a_mismatch(string? printed)
    {
        Assert.Null(InstalledTools.Read(printed));
    }

    [Fact]
    public void The_warning_names_the_tool_and_both_releases_and_what_to_do()
    {
        var warning = InstalledTools.Warning("parley-terminal", "0.41.5", "0.42.1");

        Assert.Contains("parley-terminal", warning, StringComparison.Ordinal);
        Assert.Contains("0.41.5", warning, StringComparison.Ordinal);
        Assert.Contains("0.42.1", warning, StringComparison.Ordinal);
        Assert.Contains("Install the same release", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tool_a_deployment_can_have_is_asked_about()
    {
        Assert.Equal(["parley-terminal", "parley-web-ui", "parley-mcp"], InstalledTools.Names);
    }
}
