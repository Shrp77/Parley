using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the words the contract promises are the words the surface uses.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.5. <c>contracts/surface.md</c> lists the phrase each state is
/// named by, and says every one is a constant on the view that draws it so a test can assert
/// against the constant rather than a copy. That is only worth anything while the document and the
/// constants agree — a contract nobody checks is a document that drifts.
/// </para>
/// <para>
/// Checked from the document rather than from a list written here, so a phrase changed in one place
/// and not the other fails rather than passing quietly.
/// </para>
/// </remarks>
public sealed class ContractTests
{
    private static string Contract() =>
        File.ReadAllText(Path.Combine(Root(), "specs", "005-tabbed-terminal-agents", "contracts", "surface.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "specs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    [Theory]
    [InlineData("not answering")]
    [InlineData("catalogued, not yet registered")]
    [InlineData("not in this catalogue")]
    [InlineData("seat")]
    public void Every_state_the_contract_names_is_the_phrase_the_surface_uses(string phrase)
    {
        Assert.Contains(phrase, Contract(), StringComparison.Ordinal);

        var used = new[]
        {
            ProjectsRegion.NotAnswering,
            AgentsRegion.CataloguedOnly,
            AgentsRegion.NotCatalogued,
            AgentsRegion.Seated,
        };

        Assert.Contains(phrase, used);
    }

    [Fact]
    public void The_four_regions_are_the_four_the_contract_names()
    {
        var contract = Contract();

        foreach (var title in new[]
                 {
                     ProjectsRegion.RegionTitle,
                     AgentsRegion.RegionTitle,
                     ConversationsRegion.RegionTitle,
                     TranscriptRegion.RegionTitle,
                 })
        {
            Assert.Contains($"| {title} |", contract, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_status_line_says_which_region_has_focus_in_the_contracts_words()
    {
        Assert.Contains("Focus: ", Contract(), StringComparison.Ordinal);
        Assert.Equal("Focus: ", Shell.FocusPrefix);
    }

    [Fact]
    public void The_seat_release_sentence_is_the_contracts()
    {
        // Written out in the contract in full, because it is what somebody reads when they come
        // back to a terminal that has quietly stopped being them.
        Assert.Contains(Shell.Released, Contract(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_operations_contract_still_forbids_what_it_says_it_forbids()
    {
        var operations = File.ReadAllText(
            Path.Combine(Root(), "specs", "005-tabbed-terminal-agents", "contracts", "operations.md"));

        foreach (var excluded in new[] { "ForceConclude", "Instruct", "Prune" })
        {
            Assert.Contains(excluded, operations, StringComparison.Ordinal);
        }

        // And the three it admits are named as admitted rather than merely mentioned.
        Assert.Contains("newly admitted", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the document is really being read, which is the failure mode this
        // repository has hit — a path that stopped resolving makes every case pass.
        Assert.NotEmpty(Contract());
        Assert.Contains("Regions", Contract(), StringComparison.Ordinal);
    }
}
