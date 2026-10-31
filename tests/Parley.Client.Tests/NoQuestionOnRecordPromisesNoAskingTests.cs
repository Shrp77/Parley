using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That an agent with no question from Parley on record is not shown as one Parley is about to ask (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// Found by review, lens 3 run 104. Destroying a question's conversation takes the question, and Parley does not ask that
/// agent again by itself; both reviews still said "Parley has not asked it yet", and the person waited for an asking that
/// never came. Both surfaces draw these words from <see cref="Provenance"/>.
/// </remarks>
[Unit]
public sealed class NoQuestionOnRecordPromisesNoAskingTests
{
    [Fact]
    public void No_question_on_record_says_how_to_ask_rather_than_that_Parley_will()
    {
        var words = Provenance.Of((OwnershipQuestion?)null);

        Assert.Equal(Provenance.NoQuestion, words);
        Assert.DoesNotContain("not asked it yet", words, StringComparison.Ordinal);
        Assert.Contains("Ask again", words, StringComparison.Ordinal);
    }
}
