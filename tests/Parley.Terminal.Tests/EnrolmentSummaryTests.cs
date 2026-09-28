using Grpc.Net.Client;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That enrolling from the terminal shows its progress on the agents' own rows, and says nothing at the bottom
/// unless it failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of the owner's reports, a day apart.</b> On 2026-09-27 the terminal reprinted everything the enrolment
/// wrote for a shell into the message line, where it ran together and was cut off -- and then said nothing at all
/// while each agent was started and waited for, which read as hung. On 2026-09-28: show each agent's state in the
/// Agents column as it changes, and do not mess up the screen with a status message at the bottom.
/// </para>
/// </remarks>
public sealed class EnrolmentSummaryTests
{
    private static EnrolmentOutcome Outcome(string[] registered, string[] late)
    {
        var outcome = new EnrolmentOutcome();

        foreach (var role in registered.Concat(late))
        {
            outcome.Enrolled(role);
        }

        foreach (var role in registered)
        {
            outcome.Registered(role);
        }

        foreach (var role in late)
        {
            outcome.NotRegistered(role, "started, but had not registered before this stopped looking");
        }

        return outcome;
    }

    [Fact]
    public void A_success_says_nothing_on_the_message_line()
    {
        // The rows showed it while it happened and the list drawn afterwards shows where each agent landed.
        // Must fail against the one-line summary this replaced, which restated both.
        Assert.Equal(string.Empty, Onboarding.Summary(0, Outcome(["orders", "billing"], []), string.Empty));
        Assert.Equal(string.Empty, Onboarding.Summary(0, Outcome(["orders"], ["billing"]), string.Empty));
    }

    [Fact]
    public void A_failure_says_why_in_the_commands_own_words()
    {
        // Nothing is on any row to say it, and a summary would take away which rule broke.
        var said = Onboarding.Summary(
            78, new EnrolmentOutcome(), "the broker for 'orders' did not answer.\n");

        Assert.Contains("did not answer", said, StringComparison.Ordinal);
        Assert.Contains("Nothing was enrolled.", said, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', said);
    }

    [Fact]
    public void An_agents_activity_is_shown_on_its_own_row_and_taken_off_it()
    {
        // The row is where somebody is already looking for an agent's state. Keyed by role, because an agent
        // being enrolled is a catalogue row named by its role until it registers as role@machine.
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = new Shell(directory);

        driving.Show(shell);

        var orders = new AgentRecord(
            AgentName.Create($"orders@{NodeCatalogue.Machine}"), "mcp", AgentTier.Pull, null, null, null,
            false, AgentStatus.Active);

        shell.Agents.Show([orders], NodeCatalogue.Empty, null);
        shell.Agents.Activity("orders", Parley.Setup.Registering.WaitingToRegister(7), NodeCatalogue.Empty);

        Assert.Contains(
            Drawn.Rows(shell.Agents.List),
            row => row.Contains("orders", StringComparison.Ordinal)
                   && row.EndsWith("— registering (7s of 30s)", StringComparison.Ordinal));

        shell.Agents.ClearActivity();
        shell.Agents.Show([orders], NodeCatalogue.Empty, null);

        Assert.DoesNotContain(Drawn.Rows(shell.Agents.List), row => row.Contains("registering", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_agent_chosen_is_queued_the_moment_it_is_agreed_to()
    {
        // So the screen is never silent between the person agreeing and the first agent starting.
        Assert.Equal("queued", Onboarding.Waiting);
    }
}
