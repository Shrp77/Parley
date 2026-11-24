using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That a frozen agent with a process left running beside one its node holds reads as left running, not as stopping once its
/// turn ends (SPEC.md §11.1, §14).
/// </summary>
/// <remarks>
/// The registry reads left running per attachment, so a left process beside a supervised one is <c>Unsupervised</c> and not
/// <c>OpenOutside</c>. The freeze's stop reaches only the process the node holds; the left one runs on, and only a person's
/// Kill ends it, which the terminal row offers from this phrase. Review, lens 3 run 1 on 6fde4ef3: the phrase was held only
/// for a left process with nothing supervised beside it.
/// </remarks>
[Unit]
public sealed class FrozenLeftBesideASupervisedProcessTests
{
    private const string Machine = "test";
    private const string SeatAdapter = "parley-console";

    private static readonly DateTimeOffset Then = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_frozen_agent_left_running_beside_a_supervised_process_reads_as_left_running(bool kept)
    {
        var beside = new AgentRecord(
            AgentName.Create("orders@" + Machine), "claude-code", AgentTier.Pull, null, null, null, true, AgentStatus.Active,
            Presence: AgentPresence.Running, FrozenUtc: Then, KeepFrozen: kept) with
        {
            Unsupervised = true,
            OpenOutside = false,
        };

        var row = Assert.Single(Standing.Join([beside], new HashSet<string>(StringComparer.Ordinal) { "orders" }, SeatAdapter, Machine));

        Assert.Equal(Standing.FrozenLeft, row.Standing);
    }
}
