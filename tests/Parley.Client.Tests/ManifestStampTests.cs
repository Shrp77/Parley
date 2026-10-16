using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The stamp this machine's projects are judged current by, and the hole its own documentation admits.
/// </summary>
/// <remarks>
/// <para>
/// <c>BrokerDirectory</c> decides whether to re-read by comparing a <i>stamp</i>, because reading every
/// project on every access is the cost the stamp exists to avoid.
/// </para>
/// <para>
/// <b>That limit was written down and never pinned.</b> A described limit and a tested one differ in what
/// happens next: if somebody narrows it, nothing says the note is now stale, and if it is in fact wider
/// than described, nothing says so at all. Constructed deterministically here by setting the write time by
/// hand rather than by racing a timer, so this asserts the shape of the hole rather than hoping to fall
/// into it.
/// </para>
/// <para>
/// <b>The stamp narrows the hole.</b> It is every project's configuration
/// and when each was last written. So a project appearing or going is seen by its path alone — length has
/// left the stamp entirely — and what remains missable is two edits to <i>one</i> project inside one
/// timer tick.
/// </para>
/// <para>
/// <b>And the conversion is what found the defect.</b> Stamping one file meant asking
/// <see cref="FileInfo.Exists"/> about what had become a directory: false, so every stamp was the default,
/// no stamp ever changed, and the console went on showing whatever it started with. The same mistake was
/// live in the serve watcher and in the terminal, three copies of one rule — so the rule lives in
/// <c>ProjectFolder.StampOf</c> now and all three ask it.
/// </para>
/// <para>
/// The currency behaviour itself is covered by <c>ManifestCurrencyTests</c> — which lives in the console's
/// suite, though <see cref="BrokerDirectory"/> has been this assembly's since the move. Recorded rather
/// than relocated: moving a passing test is a change with no evidence behind it.
/// </para>
/// </remarks>
public sealed class ManifestStampTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"parley-stamp-{Guid.NewGuid():N}");

    public ManifestStampTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The operating system's problem from here.
        }
    }

    private string ConfigurationOf(string name) =>
        ProjectFolder.ConfigurationFor(_root, ProjectName.Create(name));

    [Fact]
    public void A_project_added_is_always_seen_however_still_the_clock_is()
    {
        // **The half that must never fail.** It rests on something stronger than length or write
        // time: a new project is a new path, and no amount of holding the clock still can make two paths
        // one.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_root);

        Assert.Single(directory.Projects);

        var when = File.GetLastWriteTimeUtc(ConfigurationOf("alpha"));

        Write("alpha", "beta");

        // Every write time held at the original instant, so only the set of paths can distinguish these.
        File.SetLastWriteTimeUtc(ConfigurationOf("alpha"), when);
        File.SetLastWriteTimeUtc(ConfigurationOf("beta"), when);

        Assert.Equal(2, directory.Projects.Count);
    }

    [Fact]
    public void Two_edits_to_one_project_inside_one_tick_are_missed_exactly_as_documented()
    {
        // **The admitted hole, held still, and narrow.** It is two edits to the *same* project
        // inside one tick — the project set is unchanged, so only that project's write time could say
        // anything, and it has been held.
        //
        // Asserted rather than lamented. If somebody closes this — by hashing the contents, which is the
        // read the stamp exists to avoid — this test fails and says the note describing the limit is now
        // wrong, which is the only way a written-down limit stays honest.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_root);

        Assert.Equal("alpha", Assert.Single(directory.Projects).Name.Value);
        Assert.Equal("http://127.0.0.1:5301", directory.Projects[0].Endpoint);

        var when = File.GetLastWriteTimeUtc(ConfigurationOf("alpha"));

        // Substituted in place rather than rewritten through ProjectManifest.Write, which merges onto the
        // document it finds — so this is an edit to one project's own file and nothing else, which is the
        // case the documentation describes, exactly.
        File.WriteAllText(
            ConfigurationOf("alpha"),
            File.ReadAllText(ConfigurationOf("alpha"))
                .Replace("5301", "5399", StringComparison.Ordinal));

        File.SetLastWriteTimeUtc(ConfigurationOf("alpha"), when);

        // Still the old endpoint. This is the documented cost of not reading every file.
        Assert.Equal("http://127.0.0.1:5301", Assert.Single(directory.Projects).Endpoint);
    }

    [Fact]
    public void A_project_that_disappears_is_noticed_rather_than_remembered()
    {
        // Removal is a stamp of its own, which no surviving set can equal. The direction matters: a
        // directory that kept serving a project somebody has removed would be answering from a deployment
        // that no longer exists.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_root);

        Assert.Single(directory.Projects);

        File.Delete(ConfigurationOf("alpha"));

        Assert.Empty(directory.Projects);
    }

    [Fact]
    public void A_file_dropped_beside_a_project_moves_nothing()
    {
        // The other side of the stamp, and the reason it counts configurations rather than everything in
        // the tree: a project's store is written constantly while its broker serves, and a stamp that
        // moved for that would re-read every project on every tick — and, worse, would restart brokers in
        // the caller that acts on a change.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_root);

        var before = ProjectFolder.StampOf(_root);

        File.WriteAllText(
            Path.Combine(ProjectFolder.For(_root, ProjectName.Create("alpha")), "notes.txt"), "mine");

        Assert.Equal(before, ProjectFolder.StampOf(_root));
        Assert.Single(directory.Projects);
    }

    /// <summary>Writes configurations for these projects.</summary>
    /// <param name="names">The project names.</param>
    private void Write(params string[] names) =>
        ProjectManifest.Write(
            _root,
            [.. names.Select((name, index) => new ProjectSettings(
                ProjectName.Create(name),
                ProjectFolder.StoreFor(_root, ProjectName.Create(name)),
                $"http://127.0.0.1:{5301 + index}",
                null))]);
}
