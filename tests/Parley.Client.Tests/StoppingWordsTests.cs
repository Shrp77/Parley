using Parley.Client.Rendering;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>What a person is told a stop or a restart came to (SPEC.md §11.1), in the words both surfaces say.</summary>
/// <remarks>
/// A restart of an agent that is not running changes nothing, and the sentence is the only place the person learns that
/// starting it is what they wanted. Nothing held that half: with it dropped, every test passed (review, lens 3).
/// </remarks>
[Unit]
public sealed class StoppingWordsTests
{
    [Fact]
    public void A_restart_of_an_agent_that_is_not_running_says_to_start_it_and_a_stop_does_not()
    {
        Assert.Equal(
            "Nothing was asked: orders@acme is not running, so start it instead.",
            StoppingWords.Outcome("orders@acme", restart: true, asked: false));
        Assert.Equal(
            "Nothing was asked: orders@acme is not running.",
            StoppingWords.Outcome("orders@acme", restart: false, asked: false));
    }
}
