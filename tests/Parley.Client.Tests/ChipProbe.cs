using Parley.Client.Seating;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a chip comes back out of the place the surface wrote it, and not out of somebody's sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>017's review gate, lens 1, found this by probe.</b> <c>Draft.Says</c> removed each chip at its
/// <i>first</i> occurrence, and its remark claimed the consequence was benign — "an agent's name written
/// by hand in the middle of a sentence stays in the sentence, it was never a chip". That holds only while
/// the hand-written one comes after the chip. Put it before, and the words the person typed are what gets
/// removed while the addressing the surface wrote is what gets sent: the exact inversion of the method's
/// purpose.
/// </para>
/// <para>
/// <b>Last rather than first is correct by construction</b>, which is why the fix is one word.
/// <c>Completion.Fills</c> returns the line unchanged unless it ends with what was typed, so a completion
/// only ever writes at the end of the box — and a chip is therefore the last occurrence of itself at the
/// moment it is recorded.
/// </para>
/// <para>
/// Nothing here calls <c>Chip</c> through the surface, because the surface is where the spelling is built
/// (<c>"@" + chosen</c>) and this is about what the draft does with it afterwards. No test anywhere called
/// <c>Chip</c> at all before this one.
/// </para>
/// </remarks>
public sealed class ChipProbe
{
    private const string Spelling = "@orders@test";

    [Fact]
    public void A_name_typed_by_hand_before_the_chip_is_the_one_that_survives()
    {
        var draft = new Draft([]);

        // The person mentioned the agent in their sentence, then completed that same agent later in it.
        // The second occurrence is the surface's; the first is theirs.
        draft.Chip(Spelling);

        var said = draft.Says($"I asked {Spelling} yesterday, {Spelling} can you confirm?");

        Assert.Equal($"I asked {Spelling} yesterday,  can you confirm?", said);
    }

    [Fact]
    public void A_name_typed_by_hand_after_the_chip_is_kept_too()
    {
        var draft = new Draft([]);

        draft.Chip(Spelling);

        // Only one occurrence, and it is the chip: nothing of the person's is at risk here.
        var said = draft.Says($"{Spelling} can you confirm?");

        Assert.Equal("can you confirm?", said);
    }

    [Fact]
    public void Two_chips_of_different_agents_both_go()
    {
        var draft = new Draft([]);

        draft.Chip("@orders@test");
        draft.Chip("@billing@test");

        var said = draft.Says("@orders@test @billing@test are you both there?");

        Assert.Equal("are you both there?", said);
    }

    [Fact]
    public void A_chip_the_person_has_edited_is_left_in_their_words()
    {
        var draft = new Draft([]);

        draft.Chip(Spelling);

        // They deleted a character of what the completion wrote, so it is prose they have touched.
        // Leaving it is the deliberate choice: §13's envelope neutralises an @name in a payload, and
        // removing a run that no longer matches means guessing at which words were addressing.
        var said = draft.Says("@orders@tes can you confirm?");

        Assert.Equal("@orders@tes can you confirm?", said);
    }
}
