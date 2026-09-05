using Parley.Cli.Top;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That what the broker said is what the person is shown, rather than this surface's summary of it.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.6. SPEC.md §7.3 says a refusal returns work rather than a retry
/// instruction, and the shape was settled for the adapter once already: a refusal stripped of its
/// detail told an agent not to do the very thing the broker had asked for.
/// </para>
/// <para>
/// The same applies to a purge. Its verdict and its toll per table are the broker's account of what
/// it destroyed, and a surface that reduced them to "done" would be the only record of an
/// irreversible act, summarised by the thing that asked for it.
/// </para>
/// </remarks>
public sealed class BrokersWordsTests
{
    [Fact]
    public void The_purge_consequence_is_the_specifications_own_sentence()
    {
        // Quoted rather than paraphrased, because a paraphrase drifts and this one is the whole of
        // what a person is agreeing to.
        Assert.Contains("peers who did nothing wrong", Withdrawing.Purging, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", Withdrawing.Purging, StringComparison.Ordinal);
    }

    [Fact]
    public void Retiring_says_what_the_askers_will_see_rather_than_only_what_is_kept()
    {
        // The kinder outcome, and the one somebody choosing between the two needs stated: those
        // exchanges run out of time and each asker is told nobody answered.
        Assert.Contains("run out of time", Withdrawing.Retiring, StringComparison.Ordinal);
        Assert.Contains("nobody answered", Withdrawing.Retiring, StringComparison.Ordinal);
    }

    [Fact]
    public void Taking_delivery_says_that_a_clock_starts_before_it_starts_one()
    {
        // A drain marks messages delivered and starts the working window of §6 on anything owed.
        // Somebody who discovered that afterwards would have started a clock they did not know was
        // running, which is why the sentence is shown before the choice rather than after it.
        Assert.Contains("working window", Participation.DeliveryWarning, StringComparison.Ordinal);
        Assert.Contains("not a look at the inbox", Participation.DeliveryWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Destroying_a_project_says_what_goes_with_the_store()
    {
        Assert.Contains("Every conversation in it", Removing.Destroying, StringComparison.Ordinal);
        Assert.Contains("will find no broker", Removing.Destroying, StringComparison.Ordinal);
    }

    [Fact]
    public void Forgetting_says_that_it_deletes_nothing()
    {
        // The distinction the whole dialog exists to offer. A person who read only the choice
        // labels would still have to be able to tell them apart.
        Assert.Contains("deletes nothing", Removing.Forgetting, StringComparison.Ordinal);
        Assert.Contains("deletes nothing", Removing.Forget, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_says_the_agent_will_not_be_startable_until_it_registers_itself()
    {
        // The honest half of SC-003 as amended. A surface that said "added" and stopped would have
        // promised something §12.3 does not allow it to deliver.
        Assert.Equal("catalogued, not yet registered", AgentsRegion.CataloguedOnly);
    }

    [Fact]
    public void Reinstating_says_that_no_file_came_back()
    {
        Assert.Contains("No file was recreated", Withdrawing.Reinstated, StringComparison.Ordinal);
        Assert.Contains("add it again", Withdrawing.Reinstated, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seat_is_never_something_to_retire()
    {
        Assert.Contains("A person leaves a seat", Withdrawing.NotAnAgent, StringComparison.Ordinal);
    }
}
