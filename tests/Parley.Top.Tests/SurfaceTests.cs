using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That every operator operation the terminal performs happens in one place, behind its door.
/// </summary>
/// <remarks>
/// <para>
/// <c>contracts/operations.md</c> once excluded <c>Retire</c>, <c>Reinstate</c>, <c>Purge</c>,
/// <c>ForceConclude</c>, <c>Instruct</c> and <c>Prune</c> from this surface, on the argument that
/// <b>a destructive action taken by a keystroke in a full-screen application is a different risk from
/// one taken by a POST behind a page that describes it first.</b> Each was admitted since, the last three
/// in 0.26.0 on the owner's decision that every surface offers every operator operation — and each on the
/// same terms: a description first and a deliberate act.
/// </para>
/// <para>
/// <b>Enforced rather than remembered.</b> Every one of these sits on <c>BrokerClient</c>, one method
/// call away from any screen here, so what keeps the door in front of it is that the call may appear in
/// exactly one file and that file must carry the door.
/// </para>
/// <para>
/// A source scan, and deliberately not a reflection one. The operations are ordinary methods on a
/// client this assembly already references, so there is no type to look for and no binding to
/// inspect — the question is whether the call is written, and the source is where that is written.
/// </para>
/// </remarks>
public sealed class SurfaceTests
{
    /// <summary>What contracts/operations.md keeps off this surface.</summary>
    private static readonly string[] Destructive =
    [
        "RetireAsync",
        "ReinstateAsync",
        "PurgeAsync",
        "DestroyAsync",
        "ForceConcludeAsync",
        "InstructAsync",
        "PruneAsync",
    ];

    [Fact]
    public void Destroying_a_project_happens_in_one_place_and_behind_a_typed_name()
    {
        // The first destructive thing this surface did, by the owner's decision (CLAUDE.md): a
        // surface that can create a project must be able to destroy one. What the rule above
        // asks for is a description first and a deliberate act, so this holds the flag to the
        // one file that provides both -- a second path that reached it would be the keystroke the
        // rule is about -- and RemovingTests drives the typed name itself.
        //
        // **The match is the argument rather than the word, and that is the same correction twice.**
        //
        // It began as a case-insensitive scan for "destroy", which was right while one file was the
        // only place the subject came up. Withdrawing.cs then had to explain that it was admitted on
        // the same terms as destroying a project, and had to print the broker's own purge toll from
        // `PurgeOutcome.Destroyed` -- so the scan started failing on prose and on somebody else's
        // property name, and was narrowed to a word-boundary match.
        //
        // 006 walked into the same wall from the other side. TranscriptRegion.cs has to say what a
        // destroyed conversation is, and the clearest way to say it is "tell a destroy from a store
        // that lost something" -- a sentence *about* the subject, in a file that performs none of
        // it. A check that cannot tell those apart pushes people to write worse comments, which is
        // the opposite of what it is for, and its own remarks said so before it happened again.
        //
        // So the flag is now what actually performs one: the argument as it is written at a call
        // site. `Apply(manifestPath, project, destroy: true, done)` is the act; every sentence about
        // destroying something is not.
        var flag = new System.Text.RegularExpressions.Regex(
            @"\bdestroy\s*:",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        var mentioning = Sources()
            .Where(file => flag.IsMatch(File.ReadAllText(file.FullName)))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // **Only Removing.cs, because only it passes the argument.** Destroying.cs is the third
        // subject and takes the same door, but it calls `DestroyAsync` rather than a `destroy:`
        // parameter -- and that operation has its own one-file rule below, checked by name.
        Assert.Equal(["Removing.cs"], mentioning);

        var removing = File.ReadAllText(
            Sources().Single(file => file.Name == "Removing.cs").FullName);

        Assert.Contains("Type the project's name", removing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RetireAsync")]
    [InlineData("ReinstateAsync")]
    [InlineData("PurgeAsync")]
    public void Withdrawing_an_agent_happens_in_one_place_and_behind_a_typed_name(string operation)
    {
        // Admitted in 005, on the terms destroying a project was admitted on: a description first
        // and the thing's own name typed back. What keeps that true is this -- the three
        // operations may appear in exactly one file, and that file must contain the prompt.
        var mentioning = Sources()
            .Where(file => File.ReadAllText(file.FullName)
                .Contains(operation, StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Withdrawing.cs"], mentioning);

        var withdrawing = File.ReadAllText(
            Sources().Single(file => file.Name == "Withdrawing.cs").FullName);

        Assert.Contains("Type the agent's name", withdrawing, StringComparison.Ordinal);
    }

    [Fact]
    public void Destroying_a_conversation_happens_in_one_place_and_behind_a_typed_name()
    {
        // The same rule as withdrawing an agent, on a third subject. SPEC.md §14 argues that a
        // deletion happens inside the system or it happens in a SQL client with no ordering and no
        // report; the surface's half of that is that nobody reaches it by accident.
        var mentioning = Sources()
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("DestroyAsync", StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Destroying.cs"], mentioning);

        var destroying = File.ReadAllText(
            Sources().Single(file => file.Name == "Destroying.cs").FullName);

        Assert.Contains("Type the conversation's identifier", destroying, StringComparison.Ordinal);

        // And the waiting asker is named before the identifier is asked for, which is the part an
        // operator is least likely to predict: an open exchange in a destroyed conversation does
        // not orphan and is not ghosted, so whoever is waiting is never told anything at all.
        Assert.Contains("never told anything at all", destroying, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ForceConcludeAsync", "Closing.cs", "Type the conversation's identifier")]
    [InlineData("PruneAsync", "Pruning.cs", "Type the project's name")]
    [InlineData("InstructAsync", "Instructing.cs", "It may decline")]
    public void The_remaining_operator_operations_happen_in_one_place_behind_their_door(
        string operation, string home, string door)
    {
        // **Admitted in 0.26.0 on the owner's decision that every surface offers every operator operation.**
        // Until then this asserted the terminal called none of them, on 005's argument that a keystroke in a
        // full-screen application is a different risk from a POST behind a page that describes it. What that
        // argument asks for is a description and a deliberate act, which is how destroying a project,
        // withdrawing an agent and destroying a conversation were admitted — so these are held to the same
        // rule: one file, and that file carries the door. Closing a conversation and pruning take a name typed
        // back; instructing deletes nothing and its door is the description that says the agent may decline.
        var mentioning = Sources()
            .Where(file => File.ReadAllText(file.FullName).Contains(operation, StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([home], mentioning);

        Assert.Contains(
            door,
            File.ReadAllText(Sources().Single(file => file.Name == home).FullName),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // The guard against a guard that passes by finding no files. A path that stopped
        // resolving would make every case above hold vacuously, which is the failure mode this
        // repository has hit before -- a regex that silently matched nothing, making every check
        // built on it pass.
        var sources = Sources();

        Assert.NotEmpty(sources);

        // And the scan can find a name when one is there, so "not found" means absent rather
        // than unsearchable.
        Assert.Contains(
            sources,
            file => File.ReadAllText(file.FullName)
                .Contains("RegistryAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_destructive_operation_named_here_still_exists_to_be_excluded()
    {
        // The other way this rots: an operation renamed on BrokerClient leaves this checking for
        // a string nothing could contain, and the exclusion silently stops being enforced while
        // still reporting success.
        var client = typeof(Parley.Client.BrokerClient)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var operation in Destructive)
        {
            Assert.True(
                client.Contains(operation),
                $"{operation} is no longer on BrokerClient, so excluding it from the terminal "
                + "checks nothing. Rename it here, or drop it if the operation is gone.");
        }
    }

    /// <summary>Every source file the terminal surface is made of.</summary>
    /// <returns>The files.</returns>
    private static IReadOnlyList<FileInfo> Sources()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        var top = new DirectoryInfo(Path.Combine(directory!, "src", "Parley.Top"));

        Assert.True(top.Exists, $"{top.FullName} is not there, so this check reads nothing");

        return top.GetFiles("*.cs", SearchOption.AllDirectories);
    }
}
