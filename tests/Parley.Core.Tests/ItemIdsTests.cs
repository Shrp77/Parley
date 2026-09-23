using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// Which of a Request's item ids a Result is held to.
/// </summary>
/// <remarks>
/// An id longer than a Result can carry is left out, because requiring it made a question 0.4 wrote unanswerable once
/// migration carried it forward (found by the review gate, lens 1 run 1.15). The bound is the storage one, stated once.
/// </remarks>
public sealed class ItemIdsTests
{
    [Fact]
    public void The_ids_of_a_request_are_read_in_order()
    {
        Assert.Equal(
            ["w1", "q2"],
            PayloadShape.ItemIds("""{"items":[{"id":"w1","kind":"work"},{"id":"q2","kind":"question"}]}"""));
    }

    [Fact]
    public void An_id_as_long_as_a_result_can_carry_is_kept_and_one_longer_is_not()
    {
        var longest = new string('a', MessageLimits.LongestItemId);
        var over = new string('b', MessageLimits.LongestItemId + 1);

        Assert.Equal(
            [longest, "q1"],
            PayloadShape.ItemIds($$"""{"sub_questions":[{"id":"{{longest}}"},{"id":"{{over}}"},{"id":"q1"}]}"""));
    }

    [Fact]
    public void A_request_with_no_id_a_result_can_carry_has_none()
    {
        var over = new string('b', MessageLimits.LongestItemId + 1);

        Assert.Empty(PayloadShape.ItemIds($$"""{"sub_questions":[{"id":"{{over}}"},{"id":7}]}"""));
    }
}
