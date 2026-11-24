using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The Agent page's Freeze button says any start of the broker lifts an ordinary freeze (SPEC.md §14).</summary>
public sealed partial class FreezingOnTheWebTests
{
    [Fact]
    public async Task The_freeze_button_says_an_install_or_a_crash_lifts_it_too()
    {
        // Review, lens 3 on fc26cb8a: the tooltip was reworded with the confirmation (lens 3 run B on 6257cf80), and every
        // test passed with the old "a logon or a restart of the computer" put back.
        using var console = Everyone();

        var page = await AgentPageAsync(console, Orders);

        Assert.Contains("action=freeze&subject=", page, StringComparison.Ordinal);
        Assert.Contains("(a logon, a reboot, installing a new build, or a crash)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_words_before_a_freeze_follow_the_agent_named_and_not_another_one_running()
    {
        // Review, lens 3 on fc26cb8a: the page picks the named agent's row to say what holds its process; every test passed
        // with the name left out, because each had one agent running. Here another agent was left running by a node that is
        // gone, and the one named is held by its node.
        using var console = Everyone();
        var left = Row("inventory@test");
        left.Presence = Wire.AgentPresence.Running;
        left.OpenOutside = true;
        left.Unsupervised = true;
        console.Broker.Registry.Insert(0, left);
        var held = Row(Orders);
        held.Presence = Wire.AgentPresence.Running;
        console.Broker.Registry.RemoveAll(r => r.Name == Orders);
        console.Broker.Registry.Add(held);

        using var client = console.CreateClient();
        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(Address("freeze", Orders, string.Empty), Ct));

        Assert.Contains("will be stopped once its current turn ends", page, StringComparison.Ordinal);
        Assert.DoesNotContain("end it with Kill", page, StringComparison.Ordinal);
    }
}
