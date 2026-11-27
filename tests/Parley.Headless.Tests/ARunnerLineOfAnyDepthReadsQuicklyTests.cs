using System.Diagnostics;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That a runner's line is read in time that does not grow with the square of its depth.
/// </summary>
/// <remarks>
/// Lens 1 run 135: a line read with a depth bound of its own length was kept, but building a document of it took time
/// growing with the square of its depth, on the agent's pump: 200,000 deep took fifteen seconds. A fixed bound instead
/// would drop a deeper line and lose its turn (lens 2 run 139). Timed against the machine's clock, so an integration
/// test; what the line reads as is <see cref="ARunnerLineOfAnyDepthIsReadTests"/>.
/// </remarks>
[Integration]
public sealed class ARunnerLineOfAnyDepthReadsQuicklyTests
{
    [Fact]
    public void A_line_200000_deep_is_read_whole_in_well_under_a_second()
    {
        var line = ARunnerLineOfAnyDepthIsReadTests.DeepDenial(200_000);

        var watch = Stopwatch.StartNew();
        var read = ResidentAgent.RunnerEvent.Read(line);
        watch.Stop();

        Assert.NotNull(read);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"read in {watch.Elapsed}");
    }
}

/// <summary>That a runner's line is read whatever its depth, and its fields as they were read from a document.</summary>
/// <remarks>The untimed half of <see cref="ARunnerLineOfAnyDepthReadsQuicklyTests"/>.</remarks>
[Unit]
public sealed class ARunnerLineOfAnyDepthIsReadTests
{
    /// <summary>A result whose refused tool call carried an input nested this deep.</summary>
    internal static string DeepDenial(int depth) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"deep\",\"num_turns\":2,"
        + "\"total_cost_usd\":5,\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_input\":"
        + string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "0" + new string('}', depth) + "},{}],\"after\":[[1]]}";

    [Fact]
    public void A_line_200000_deep_is_read_whole()
    {
        var read = ResidentAgent.RunnerEvent.Read(DeepDenial(200_000));

        Assert.NotNull(read);
        Assert.Equal("result", read.Type);
        Assert.Equal("deep", read.Result);
        Assert.Equal(2, read.PermissionDenials);
        Assert.Equal(2, read.ModelTurns);
        Assert.Equal(5, read.CostUsd);
    }

    [Fact]
    public void A_line_that_is_not_json_is_still_not_read()
    {
        Assert.Null(ResidentAgent.RunnerEvent.Read(DeepDenial(3)[..^1]));
        Assert.Null(ResidentAgent.RunnerEvent.Read(DeepDenial(3) + "{}"));
        Assert.Null(ResidentAgent.RunnerEvent.Read("[1]"));
        Assert.Null(ResidentAgent.RunnerEvent.Read(string.Empty));
    }

    [Fact]
    public void Fields_read_as_they_did_from_a_document()
    {
        // The last copy of a member, a name that is not valid text passed over with its value, a value that is not valid
        // text absent, and a field of the wrong kind absent.
        var read = ResidentAgent.RunnerEvent.Read(
            """{"type":"x","type":"result","\uD800":{"is_error":true},"is_error":true,"session_id":"s\uD800","subtype":7,"stop_reason":"end","permission_denials":{}}""");

        Assert.NotNull(read);
        Assert.Equal("result", read.Type);
        Assert.True(read.IsError);
        Assert.Null(read.SessionId);
        Assert.Null(read.Subtype);
        Assert.Equal("end", read.StopReason);
        Assert.Equal(0, read.PermissionDenials);
    }
}
