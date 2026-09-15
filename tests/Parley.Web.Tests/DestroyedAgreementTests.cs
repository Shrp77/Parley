using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That both surfaces give one account of a destroy.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.8. Run 3.2 checked that neither surface derives its own answer
/// where a shared one exists; this asks it of the newest answer, which is the one with the least
/// history behind it and the strongest reason to be right.
/// </para>
/// <para>
/// <b>The record is the only account of those rows that will ever exist.</b> Two surfaces
/// describing it differently would be two accounts of an event with nothing left to settle which
/// was accurate — and unlike every other disagreement between them, this one could not be resolved
/// by looking at the thing described.
/// </para>
/// </remarks>
public sealed class DestroyedAgreementTests
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

    private static string Sources(params string[] under)
    {
        var at = Path.Combine([Root(), .. under]);

        return string.Join(
            "\n",
            Directory.EnumerateFiles(at, "*.cs*", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                   StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                .Select(File.ReadAllText));
    }

    [Fact]
    public void Both_surfaces_compose_the_account_through_the_one_helper()
    {
        var console = Sources("src", "Parley.Web");
        var terminal = Sources("src", "Parley.Terminal");

        Assert.Contains("Reading.Gone(", console, StringComparison.Ordinal);
        Assert.Contains("Reading.Gone(", terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_surface_writes_its_own_sentence_about_a_destroy()
    {
        // The way round the helper: a page or a region assembling the same facts into its own
        // words. It would read correctly and it would be a second account.
        foreach (var (where, source) in new[]
                 {
                     ("console", Sources("src", "Parley.Web")),
                     ("terminal", Sources("src", "Parley.Terminal")),
                 })
        {
            Assert.False(
                source.Contains("was destroyed on", StringComparison.Ordinal),
                $"the {where} composes its own account of a destroy");

            Assert.False(
                source.Contains("Nothing of what was said", StringComparison.Ordinal),
                $"the {where} composes its own account of a destroy");
        }
    }

    [Fact]
    public void The_account_names_what_was_lost_and_never_what_was_said()
    {
        // The line the helper must not cross, checked on what it actually produces rather than on
        // where it lives. A rendering that implied recoverable content would suggest something is
        // retrievable when nothing is.
        var said = string.Join(
            "\n",
            Reading.Gone(new DestroyedConversation(
                "a-discussion",
                DateTimeOffset.UnixEpoch,
                3,
                [AgentName.Create("orders@test")])));

        Assert.Contains("a-discussion", said, StringComparison.Ordinal);
        Assert.Contains("3 messages", said, StringComparison.Ordinal);
        Assert.DoesNotContain("…", said, StringComparison.Ordinal);
        Assert.DoesNotContain("...", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_surfaces_keep_a_destroyed_conversation_distinct_from_an_empty_one()
    {
        // The distinction is the whole of FR-006 and it is easy to lose by widening one message to
        // cover both. Each surface has to say two different things.
        var terminal = Sources("src", "Parley.Terminal");
        var console = Sources("src", "Parley.Web");

        Assert.Contains("has no messages", terminal, StringComparison.Ordinal);
        Assert.Contains("was destroyed", terminal, StringComparison.Ordinal);

        Assert.Contains("Nothing was recorded under this identifier", console, StringComparison.Ordinal);
        Assert.Contains("was destroyed", console, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_reading_both_trees()
    {
        Assert.Contains("BrokerClient", Sources("src", "Parley.Web"), StringComparison.Ordinal);
        Assert.Contains("BrokerClient", Sources("src", "Parley.Terminal"), StringComparison.Ordinal);
    }
}
