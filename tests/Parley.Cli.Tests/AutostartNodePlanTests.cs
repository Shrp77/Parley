using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>The entry that runs only a node, as each supervisor would be handed it (SPEC.md §4.1.2).</summary>
[Unit]
public sealed class AutostartNodePlanTests
{
    private static AutostartPlan Plan() =>
        AutostartPlan.DescribeNode(
            Path.Combine(Path.GetTempPath(), "a root"),
            "acme",
            "http://127.0.0.1:19100",
            Path.Combine(Path.GetTempPath(), "a root", "node.json"),
            executable: "parley");

    [Fact]
    public void It_is_named_for_its_project_so_it_stands_beside_a_broker()
    {
        Assert.Equal("parley-node-acme", Plan().Label);
        Assert.True(Plan().IsNode);
        Assert.False(AutostartPlan.Describe("root", executable: "parley").IsNode);
    }

    [Fact]
    public void It_runs_a_node_that_keeps_trying_and_no_broker()
    {
        var arguments = Plan().Arguments;

        Assert.StartsWith("up ", arguments, StringComparison.Ordinal);
        Assert.Contains("--project acme", arguments, StringComparison.Ordinal);
        Assert.Contains("--broker http://127.0.0.1:19100", arguments, StringComparison.Ordinal);
        Assert.Contains("--keep-trying", arguments, StringComparison.Ordinal);
        Assert.Contains("--log", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("serve", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--with-web-ui", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_with_spaces_survive_the_split_a_supervisor_makes()
    {
        var parts = AutostartPlan.Split(Plan().Arguments);

        Assert.Contains(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "a root"), parts);
        Assert.Contains(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "a root", "node.json"), parts);
    }

    [Fact]
    public void Systemd_does_not_restart_a_node_the_broker_refused()
    {
        var unit = Plan().SystemdUnit();

        Assert.Contains("RestartPreventExitStatus=78", unit, StringComparison.Ordinal);
        Assert.Contains("Parley node", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartPreventExitStatus", AutostartPlan.Describe("root", executable: "parley").SystemdUnit(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launchd_does_not_restart_a_node_the_broker_refused()
    {
        Assert.DoesNotContain("KeepAlive", Plan().LaunchAgentPlist(), StringComparison.Ordinal);
        Assert.Contains("KeepAlive", AutostartPlan.Describe("root", executable: "parley").LaunchAgentPlist(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_scheduler_is_handed_the_same_arguments()
    {
        Assert.Contains("--keep-trying", Plan().ScheduledTaskXml("someone"), StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_is_said_last()
    {
        var plan = AutostartPlan.DescribeNode("root", "acme", "http://127.0.0.1:19100", "node.json", "parley", hidden: true);

        Assert.EndsWith("--hidden", plan.Arguments, StringComparison.Ordinal);
    }
}
