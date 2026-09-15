using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// That the two documents a person reads before touching this repository describe what it is now.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.6, and the last of them. Run 3.3 checked the feature's own
/// documents against each other. This checks the two that outlive the feature: `CLAUDE.md`, which
/// the next person working here reads first, and `SPEC.md`, which somebody building another
/// implementation reads instead of the code.
/// </para>
/// <para>
/// <b>These age in a particular way.</b> Nobody rereads them to check they are still true; they are
/// consulted for one fact at a time, and a wrong one is believed. 005 left `CLAUDE.md` describing
/// four regions on one screen for a day after that stopped being what the terminal did.
/// </para>
/// </remarks>
public sealed partial class ProjectRecordTests
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

    private static string Read(string name) => File.ReadAllText(Path.Combine(Root(), name));

    [Fact]
    public void The_repository_notes_name_the_active_feature_and_its_version()
    {
        // The first thing somebody arriving needs: which feature is in progress and what number it
        // ships as. A note naming the previous one sends them to the wrong plan.
        var notes = Read("CLAUDE.md");

        Assert.Contains("specs/006-three-view-operator/", notes, StringComparison.Ordinal);
        Assert.Contains(Shipping(), notes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_the_notes_claim_is_the_version_the_build_carries()
    {
        // The one convention in CLAUDE.md the build cannot catch, which is why it is written down
        // there — and therefore the one most worth checking from outside.
        //
        // Derived rather than written out, and that is the whole substance of this test. Both
        // halves used to name 0.18.0 as a literal, which checked that somebody had typed the same
        // number twice here rather than that the two documents agree — so the first patch bump
        // failed it, in the file that is not the one at fault, and the cheapest way past a test
        // like that is to update the literal without reading the file it is about.
        var props = Read("Directory.Build.props");

        Assert.Contains(
            $"<VersionPrefix>{Shipping()}</VersionPrefix>", props, StringComparison.Ordinal);
    }

    /// <summary>The version this build ships as, read from where the convention puts it.</summary>
    /// <returns>The version, as <c>Directory.Build.props</c> declares it.</returns>
    /// <remarks>
    /// The props file is the source and <c>CLAUDE.md</c> is what has to agree with it, rather than
    /// the other way round: the build carries this number into every package whatever any document
    /// says, so a disagreement is the document being wrong.
    /// </remarks>
    private static string Shipping()
    {
        var props = Read("Directory.Build.props");
        var version = VersionPrefix().Match(props);

        // Guarded, because a regex that matches nothing would make both tests above pass by
        // asserting that two files each contain the empty string.
        Assert.True(version.Success, "Directory.Build.props declares no VersionPrefix");

        return version.Groups[1].Value;
    }

    [GeneratedRegex(@"<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [Fact]
    public void The_notes_record_what_the_feature_reversed_rather_than_only_what_it_built()
    {
        // The most valuable thing in that file is the argument, not the inventory. 006 reversed a
        // decision 005 recorded as settled, and a note that listed the new operations without
        // saying that would leave the next reader to rediscover why the old reasoning was wrong.
        var notes = Read("CLAUDE.md");

        Assert.Contains("12.3", notes, StringComparison.Ordinal);
        Assert.Contains("T039", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_specification_carries_the_four_operations_and_the_state_they_need()
    {
        var spec = Read("SPEC.md");

        foreach (var required in new[]
                 {
                     "`archive(conversation)`",
                     "`unarchive(conversation)`",
                     "`destroy(conversation)`",
                     "`start(agent)`",
                     "archived_utc",
                     "destroyed_conversations",
                 })
        {
            Assert.Contains(required, spec, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_specification_says_what_a_waiting_agent_sees_when_a_conversation_is_destroyed()
    {
        // The consequence an operator is least likely to predict, and the one a surface has to be
        // able to quote. If the document stopped saying it, the surface would be the only place it
        // was written down — and a surface is not what another implementation reads.
        var spec = Read("SPEC.md");

        Assert.Contains("never told anything at all", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_still_stands_alone()
    {
        // It is the one document that must not require the specification to be readable, which is
        // a standing rule here rather than a preference for 006.
        var readme = Read("README.md");

        Assert.DoesNotContain("SPEC.md §", readme, StringComparison.Ordinal);
        Assert.Contains("parley terminal", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_reading_the_documents()
    {
        Assert.True(Read("CLAUDE.md").Length > 10_000);
        Assert.True(Read("SPEC.md").Length > 10_000);
    }
}
