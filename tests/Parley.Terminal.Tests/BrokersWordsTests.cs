using Xunit;

namespace Parley.Terminal.Tests;

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
    public void Retiring_says_what_becomes_of_the_work_it_holds_rather_than_only_what_is_kept()
    {
        // The kinder outcome, and the one somebody choosing between the two needs stated. Nothing
        // times out (§5.3), so the work stays open and is put in front of the person who retired it.
        Assert.Contains("stays open", Withdrawing.Retiring, StringComparison.Ordinal);
        Assert.Contains("flagged", Withdrawing.Retiring, StringComparison.Ordinal);
        Assert.DoesNotContain("run out of time", Withdrawing.Retiring, StringComparison.Ordinal);
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
        // The honest half. A surface that said "added" and stopped would have
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
