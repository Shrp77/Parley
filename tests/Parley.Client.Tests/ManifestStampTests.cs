using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The stamp a manifest is judged current by, and the hole its own documentation admits.
/// </summary>
/// <remarks>
/// <para>
/// <c>BrokerDirectory</c> decides whether to re-read by comparing a <i>stamp</i> — the file's
/// length and its last write time — because reading the file on every access is the cost the
/// stamp exists to avoid. The remarks on <c>StampOf</c> state the accepted limit: Windows updates
/// a write time on the system timer tick rather than per write, so two edits inside about fifteen
/// milliseconds share a timestamp, and if they also share a *length* the second is missed.
/// </para>
/// <para>
/// <b>That limit was written down and never pinned.</b> A described limit and a tested one differ
/// in what happens next: if somebody narrows it, nothing says the note is now stale, and if it is
/// in fact wider than described, nothing says so at all. Constructed deterministically here by
/// setting the write time by hand rather than by racing a timer, so this asserts the shape of the
/// hole rather than hoping to fall into it.
/// </para>
/// <para>
/// The currency behaviour itself is covered by <c>ManifestCurrencyTests</c> — which lives in the
/// console's suite, though <see cref="BrokerDirectory"/> has been this assembly's since the move.
/// Recorded rather than relocated: moving a passing test is a change with no evidence behind it.
/// </para>
/// </remarks>
public sealed class ManifestStampTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"parley-stamp-{Guid.NewGuid():N}");

    private readonly string _path;

    public ManifestStampTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "projects.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The operating system's problem from here.
        }
    }

    [Fact]
    public void An_edit_that_changes_the_length_is_always_seen()
    {
        // The half that must never fail, and the reason length is in the stamp at all: a write
        // time alone would miss this whenever the clock had not ticked.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Single(directory.Projects);

        var when = File.GetLastWriteTimeUtc(_path);

        Write("alpha", "beta");

        // The clock held still, so only the length can distinguish these.
        File.SetLastWriteTimeUtc(_path, when);

        Assert.Equal(2, directory.Projects.Count);
    }

    [Fact]
    public void An_edit_of_identical_length_inside_one_tick_is_missed_exactly_as_documented()
    {
        // The admitted hole, held still. Two manifests of the same size, written at the same
        // recorded instant: nothing cheap distinguishes them, and the second is not seen.
        //
        // Asserted rather than lamented. If somebody closes this -- by hashing the contents, which
        // is the read the stamp exists to avoid -- this test fails and says the note describing
        // the limit is now wrong, which is the only way a written-down limit stays honest.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Equal("alpha", Assert.Single(directory.Projects).Name.Value);

        var when = File.GetLastWriteTimeUtc(_path);
        var size = new FileInfo(_path).Length;

        // Substituted in place rather than rewritten through ProjectManifest.Write, which merges
        // onto the document it finds and so does not promise an identical size for an identically
        // sized name -- it differed by a byte, which would have made this test pass by accident,
        // on the length rather than on the hole it is about. A same-length replacement of the text
        // is the case the documentation describes, exactly.
        File.WriteAllText(_path, File.ReadAllText(_path).Replace("alpha", "gamma", StringComparison.Ordinal));

        File.SetLastWriteTimeUtc(_path, when);

        Assert.Equal(size, new FileInfo(_path).Length);

        // Still the old name. This is the documented cost of not reading the file.
        Assert.Equal("alpha", Assert.Single(directory.Projects).Name.Value);
    }

    [Fact]
    public void A_manifest_that_disappears_is_noticed_rather_than_remembered()
    {
        // Deletion is a stamp of its own -- the default -- which no present file can equal. The
        // direction matters: a directory that kept serving the projects of a file somebody has
        // removed would be answering from a deployment that no longer exists.
        Write("alpha");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Single(directory.Projects);

        File.Delete(_path);

        Assert.Empty(directory.Projects);
    }

    /// <summary>Writes a manifest naming these projects.</summary>
    /// <param name="names">The project names, each five characters so sizes can be held equal.</param>
    private void Write(params string[] names) =>
        ProjectManifest.Write(
            _path,
            [.. names.Select((name, index) => new ProjectSettings(
                ProjectName.Create(name),
                Path.Combine(_folder, $"{name}.db"),
                $"http://127.0.0.1:{5301 + index}",
                null))]);
}
