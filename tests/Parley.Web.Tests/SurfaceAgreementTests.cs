using Parley.Client.Views;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console and the terminal say the same things about the same deployment.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.2. Run 3.1 asked whether the shared library is the smallest
/// surface that could serve both; this asks the other half — whether either surface has quietly
/// gone round it.
/// </para>
/// <para>
/// <b>Here rather than in the terminal's own tests, because only this project may see both.</b>
/// <c>DriftTests</c> compares the operations each surface binds, which catches an operation one
/// of them cannot reach at all. It cannot catch a surface that reaches the operation and then
/// derives its own answer from the result, which is the failure that actually happened before
/// 004 pulled the shared assembly out: two implementations of "who is in this conversation",
/// disagreeing.
/// </para>
/// </remarks>
[Integration]
public sealed class SurfaceAgreementTests
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

    [Theory]
    [InlineData("Standing.Join")]
    [InlineData("ConversationsView.Sorted")]
    [InlineData("ConversationsView.Hidden")]
    public void Both_surfaces_reach_the_shared_derivation_for_what_both_show(string call)
    {
        // Each of these decides something a person compares between the two screens: what an agent
        // is doing, what order the conversations are in, and how many are being kept out of sight.
        // A surface that computed any of them itself would be a second answer.
        var console = Sources("src", "Parley.Web");
        var terminal = Sources("src", "Parley.Terminal");

        Assert.Contains(call, console + terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_surface_sorts_a_conversation_list_by_itself()
    {
        // The specific way round it: an OrderBy over conversations, in either surface, is a second
        // ordering that agrees with the shared one until somebody changes one of them.
        foreach (var (where, source) in new[]
                 {
                     ("console", Sources("src", "Parley.Web")),
                     ("terminal", Sources("src", "Parley.Terminal")),
                 })
        {
            foreach (var line in source.Split('\n'))
            {
                if (line.Contains("Conversations", StringComparison.Ordinal)
                    && (line.Contains(".OrderBy", StringComparison.Ordinal)
                        || line.Contains(".OrderByDescending", StringComparison.Ordinal)))
                {
                    Assert.Fail($"the {where} orders conversations itself: {line.Trim()}");
                }
            }
        }
    }

    [Fact]
    public void Neither_surface_writes_a_local_time_by_itself()
    {
        // Both must render a stored UTC instant in the reader's zone, and there is one helper for
        // it. A second conversion is how the two come to disagree about what "this morning" means.
        foreach (var (where, source) in new[]
                 {
                     ("console", Sources("src", "Parley.Web")),
                     ("terminal", Sources("src", "Parley.Terminal")),
                 })
        {
            Assert.False(
                source.Contains("DateTimeOffset.Now.ToOffset", StringComparison.Ordinal),
                $"the {where} converts an instant itself");
        }
    }

    [Fact]
    public void The_phrases_a_person_reads_come_from_one_place()
    {
        // An agent's standing is the thing a person is most likely to compare across the two
        // screens, because it is the answer to "why is this not running". Two sources of wording
        // would let them disagree while both were correct.
        var console = Sources("src", "Parley.Web");
        var terminal = Sources("src", "Parley.Terminal");

        foreach (var phrase in new[]
                 {
                     Standing.Running, Standing.Idle, Standing.OnlyWhenOpened,
                     Standing.NoMachine, Standing.NeverRun,
                 })
        {
            Assert.DoesNotContain($"\"{phrase}\"", console, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{phrase}\"", terminal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_check_is_reading_both_trees()
    {
        // Every assertion above passes against a path that resolved to nothing, which is the trap
        // the schema extraction had to be guarded against and the same one here.
        Assert.Contains("BrokerClient", Sources("src", "Parley.Web"), StringComparison.Ordinal);
        Assert.Contains("BrokerClient", Sources("src", "Parley.Terminal"), StringComparison.Ordinal);
    }
}
