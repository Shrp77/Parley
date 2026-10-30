using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the launch configuration's key is spelled in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this enforces is the feature's only structural guarantee.</b> A repository may now hold
/// an entry per project, and every decision about which entry to update, which to leave alone and
/// which to delete is made from its key. Three files spelled the key before this feature and the two
/// readers among them disagreed with the writer the moment a second spelling existed — which is how a
/// scan comes to report an enrolled repository as enrolled nowhere.
/// </para>
/// <para>
/// <b>Written as an enumeration rather than a ban, because two of the occurrences are not keys at
/// all.</b> The broker's executable is called <c>parley</c> and the terminal's window is titled
/// <c>parley</c>; both are the same six letters and neither is a server key. A check that forbade the
/// literal outright would have to be suppressed in several places, and a suppressed check is one
/// nobody reads. This one names what is allowed, so a new occurrence anywhere fails and somebody
/// decides which of the four things it is.
/// </para>
/// <para>
/// <b>There are four illustrations.</b> The README,
/// the plugin's setup instructions and the console's registration page show a launch configuration, and the
/// adapter's own header comment shows a launch configuration too, and an illustration that disagrees with the tool is read
/// by exactly the person who has not used the tool yet.
/// </para>
/// <para>
/// The <c>SurfaceTests</c> shape, and for its reason: this repository has already had a rule of this
/// kind broken by a helper whose <i>name</i> merely contained the word.
/// </para>
/// </remarks>
[Integration]
public sealed class OneKeyRuleTests
{
    /// <summary>Where the literal may appear, and what it is doing there.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["LaunchKey.cs"] = "the key itself, which is the whole point of the type",
        ["AutostartPlan.cs"] = "the broker's executable name, when nothing else names it",
        ["Program.cs"] = "the adapter's header comment, illustrating a launch configuration",
    };

    [Fact]
    public void The_server_key_is_spelled_in_one_file()
    {
        var offenders = Sources()
            .Where(file => File.ReadAllText(file.FullName).Contains("\"parley\"", StringComparison.Ordinal))
            .Select(file => file.Name)
            .Where(name => !Allowed.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"the literal \"parley\" appears in {string.Join(", ", offenders)}. If it is a launch "
            + "configuration key it belongs in LaunchKey; if it is something else, say which in "
            + "OneKeyRuleTests.Allowed.");
    }

    [Fact]
    public void Every_file_the_rule_excuses_still_exists_and_still_says_it()
    {
        // A rule whose exceptions have gone stale passes by vacuity, which is the failure the spec
        // suite's own extraction guard was written for. If one of these files stops containing the
        // literal, the excuse should go rather than sit here making the check look narrower than it is.
        var saying = Sources()
            .Where(file => File.ReadAllText(file.FullName).Contains("\"parley\"", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (file, why) in Allowed)
        {
            Assert.True(saying.Contains(file), $"{file} no longer contains the literal ({why}).");
        }
    }

    [Fact]
    public void The_derived_prefix_is_spelled_only_where_the_key_lives()
    {
        var offenders = Sources()
            .Where(file => File.ReadAllText(file.FullName).Contains("\"parley-\"", StringComparison.Ordinal))
            .Select(file => file.Name)
            .Where(name => !string.Equals(name, "LaunchKey.cs", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"the prefix \"parley-\" is built by hand in {string.Join(", ", offenders)}. "
            + "LaunchKey.For is what builds a key.");
    }

    /// <summary>Every shipped source file.</summary>
    /// <returns>The files under <c>src</c>, excluding build output.</returns>
    private static IReadOnlyList<FileInfo> Sources()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        var source = new DirectoryInfo(Path.Combine(directory!, "src"));

        Assert.True(source.Exists, $"{source.FullName} is not there, so this check reads nothing");

        var files = source.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // Guards the extraction itself: a glob that silently matched nothing would make every
        // assertion above pass while reading no source at all.
        Assert.NotEmpty(files);

        return files;
    }
}
