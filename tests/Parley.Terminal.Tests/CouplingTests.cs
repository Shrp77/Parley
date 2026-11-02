using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the regions know nothing about one another, and the shell is the only thing that
/// coordinates them.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.4. Four regions on one screen is four things that could reach
/// into each other, and the temptation is specific: the agents region wants the selected project,
/// the transcript wants the selected conversation. Each of those is one line, and each would make
/// the region untestable on its own and unmovable in the layout.
/// </para>
/// <para>
/// The shell is where they meet — it reads the selection, calls the broker and hands each region
/// what to draw — which is also why every region's <c>Show</c> takes data rather than a client.
/// </para>
/// </remarks>
[Integration]
public sealed class CouplingTests
{
    private static readonly string[] Regions =
    [
        "ProjectsRegion.cs",
        "AgentsRegion.cs",
        "ConversationsRegion.cs",
        "TranscriptRegion.cs",
    ];

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(Root(), "src", "Parley.Terminal", name));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    [Fact]
    public void No_region_names_another()
    {
        foreach (var region in Regions)
        {
            var text = Read(region);

            foreach (var other in Regions.Where(other => other != region))
            {
                var name = Path.GetFileNameWithoutExtension(other);

                Assert.False(
                    text.Contains(name, StringComparison.Ordinal),
                    $"{region} names {name}. The shell is what coordinates the regions; a region "
                    + "that reaches another cannot be laid out anywhere else or tested on its own.");
            }
        }
    }

    [Fact]
    public void No_region_names_the_shell()
    {
        // The other direction, and the worse one: a region that called back into the shell would
        // make the two mutually dependent, and the shell is what holds the seat, the manifest and
        // every broker client.
        foreach (var region in Regions)
        {
            Assert.False(
                Read(region).Contains("Shell", StringComparison.Ordinal),
                $"{region} names the shell. Regions raise events; the shell decides what they mean.");
        }
    }

    [Fact]
    public void No_region_reaches_a_broker_itself()
    {
        // Every region is handed what to draw. One that fetched its own would read at a moment
        // nobody chose, and its read would not be contained by the shell's queue -- so two regions
        // could draw answers about two different projects at once.
        foreach (var region in Regions)
        {
            var text = Read(region);

            foreach (var forbidden in new[] { "BrokerClient", "BrokerDirectory", "ClientFor" })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{region} names {forbidden}. Regions are given what to draw.");
            }
        }
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the files really are being read, and they really do contain the word the
        // scan would find if a region did name another.
        Assert.All(Regions, region => Assert.NotEmpty(Read(region)));

        Assert.Contains("AgentsRegion", Read("AgentsRegion.cs"), StringComparison.Ordinal);
    }
}
