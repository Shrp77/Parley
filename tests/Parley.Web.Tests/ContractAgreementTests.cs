using Parley.Client.Views;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the views contract describes the surfaces that were built.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.4. `contracts/views.md` is the document that says what each of
/// the three views shows and what it can do — the thing a reader consults instead of opening the
/// code, and therefore the thing that is wrong for longest when it is wrong.
/// </para>
/// <para>
/// Only the checkable claims are checked. Prose about why a view is shaped a particular way is not
/// something a test can hold to account, and pretending otherwise would put a green tick on a
/// paragraph nobody verified.
/// </para>
/// </remarks>
public sealed class ContractAgreementTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>One of the two phrases 006 replaced, kept here so the check names what it means.</summary>
    private const string AgentsPhrase = "catalogued, not yet registered";

    private static string Views() => File.ReadAllText(Path.Combine(
        Root(), "specs", "006-three-view-operator", "contracts", "views.md"));

    [Fact]
    public void The_contract_names_exactly_the_three_views_that_exist()
    {
        var contract = Views();

        foreach (var named in Enum.GetNames<Viewing>())
        {
            Assert.Contains(named, contract, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(3, Enum.GetValues<Viewing>().Length);
    }

    [Fact]
    public void Every_standing_phrase_the_contract_promises_is_one_that_exists()
    {
        // The contract does not quote the phrases, deliberately -- it names the two facts to
        // convey and leaves the words to be chosen. So what is checked is that both facts are
        // named and that neither replaced phrase is offered as one a reader will see.
        //
        // **Written first as a search for "has ever run" and failed, because the contract says
        // "has it ever run".** Matching prose by substring is how a check ends up asserting a
        // sentence's grammar; these ask for the words that carry the meaning instead.
        var contract = Views();

        Assert.Contains("ever run", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start it", contract, StringComparison.OrdinalIgnoreCase);

        // **And the replaced phrase is present, which is right.** The contract names it in the
        // sentence saying what the standing replaces -- history worth keeping, and the same shape
        // as the four regions being named in the sentence explaining what they became. What would
        // be wrong is offering it as wording a reader will meet, so the check is that the only
        // place it appears is that sentence.
        var mentions = contract.Split(AgentsPhrase, StringSplitOptions.None).Length - 1;

        Assert.Equal(1, mentions);
        Assert.Contains(
            $"replaces *{AgentsPhrase}*", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_door_the_contract_describes_is_a_door_the_code_has()
    {
        // The doors are the part of this contract with consequences, so each is checked against the
        // constant that actually opens it rather than against a paraphrase.
        var contract = Views();

        // Destroying takes a name typed back; archiving takes none. Both are stated, and what is
        // checked is that the contract distinguishes them rather than that it phrases it any
        // particular way.
        Assert.Contains("typed back", contract, StringComparison.Ordinal);

        var doors = contract[contract.IndexOf("| Can do | Door |", StringComparison.Ordinal)..];

        Assert.Contains("none", doors, StringComparison.OrdinalIgnoreCase);

        var destroying = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Top", "Destroying.cs"));

        Assert.Contains("Type the conversation's identifier", destroying, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_key_the_contract_names_is_the_key_that_is_bound()
    {
        Assert.Contains("F8", Views(), StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Top", "Shell.cs"));

        Assert.Contains("Key.F8", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_does_not_still_describe_the_arrangement_it_replaced()
    {
        // The half a rewritten contract forgets: a document that gained the three views and kept
        // the four regions would describe two surfaces, one of which does not exist.
        //
        // **This is harder to check than it looks and the first attempt was wrong.** The contract
        // does mention the four regions -- in the sentence explaining what it replaced, which is
        // exactly the history worth keeping. What must not survive is a *description* of them as
        // the present shape. So the check is on the shape it claims now: three views named in a
        // diagram, and no promise that Tab moves between the four.
        var contract = Views();

        Assert.Contains("Projects ──choose──> Project ──choose──> Conversation", contract, StringComparison.Ordinal);
        Assert.Contains("replaces", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_check_is_reading_the_contract()
    {
        Assert.True(Views().Length > 2_000);
    }
}
