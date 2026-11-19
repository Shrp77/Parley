using System.Diagnostics;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>That the adapter names the process that started it, so a person can end one a node that is gone left behind (SPEC.md §11.1).</summary>
/// <remarks>
/// A label and never authority: what is held is that it is a real process that is not the adapter itself, and that an adapter
/// that cannot be told says nothing rather than something wrong. Which process it is, is the operating system's answer.
/// </remarks>
[Integration]
public sealed class TheAdapterSaysWhichProcessHostsItTests
{
    [Fact]
    public void It_is_a_running_process_other_than_the_adapter()
    {
        var host = HostProcess.Id();

        Assert.NotNull(host);
        Assert.NotEqual(Environment.ProcessId, host);

        using var named = Process.GetProcessById((int)host.Value);

        Assert.False(named.HasExited);
    }
}
