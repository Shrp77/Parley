using Parley.Setup;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That a repository already answering in one project can be added to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner: it could not be done at all, and a different name did not help.</b>
/// SPEC.md §4.1 permits it — the protocol has no concept of a project, and membership is which
/// endpoint an agent connects to — and 009 built the deployment tooling for it. What stopped it was
/// one word in this surface: the terminal enrolled with <c>Unattended: true</c> always, and FR-013
/// refuses a second unattended start for one workspace. So the second project was refused by a
/// rule that was never about it.
/// </para>
/// <para>
/// <b>A different name cannot help and must not.</b> The rule is keyed on the workspace, because
/// the hazard is a node starting a client with that workspace as its working directory — two of
/// them is two models editing one checkout, and renaming the agent does not make it two checkouts.
/// So what changes is what gets written, not what gets compared.
/// </para>
/// </remarks>
public sealed class SecondProjectTests
{
    private static Enrolment.Shared Already => new("alpha", "orders");

    [Fact]
    public void The_second_project_is_told_it_gets_no_catalogue_line()
    {
        var said = Adding.Describe("C:/repo", "C:/store/beta-node.json", "beta", Already);

        // The launch entry, which is the whole of what makes it answer in this project.
        Assert.Contains(".mcp.json", said, StringComparison.Ordinal);

        // And not the catalogue, which is the one thing it may not have twice.
        Assert.DoesNotContain("beta-node.json", said, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_which_project_holds_the_unattended_start()
    {
        var said = Adding.Describe("C:/repo", "C:/store/beta-node.json", "beta", Already);

        // Actionable rather than merely true. Somebody who wants this project to be the one that
        // starts it has to know which project to take it away from.
        Assert.Contains("alpha", said, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_the_agent_still_ends_up_registered_here()
    {
        // The half that makes this worth doing rather than a documented limitation. One working
        // directory has one launch configuration and a client brings up every server in it, so a
        // start through the other project registers the agent in both.
        var said = Adding.Describe("C:/repo", "C:/store/beta-node.json", "beta", Already);

        Assert.Contains("registers", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_first_enrolment_still_names_both_files()
    {
        // The change must not cost the common case its description.
        var said = Adding.Describe("C:/repo", "C:/store/alpha-node.json", "alpha", already: null);

        Assert.Contains(".mcp.json", said, StringComparison.Ordinal);
        Assert.Contains("alpha-node.json", said, StringComparison.Ordinal);
        Assert.Contains(Adding.Starts, said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_description_does_not_read_as_a_refusal()
    {
        // It is not one. The repository is being added, and every word suggesting otherwise sends
        // somebody looking for a problem to fix.
        var said = Adding.Shared(Already, "beta");

        foreach (var wrong in new[] { "cannot", "refused", "failed", "not allowed" })
        {
            Assert.DoesNotContain(wrong, said, StringComparison.OrdinalIgnoreCase);
        }
    }
}
