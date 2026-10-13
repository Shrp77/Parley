using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That nothing 006 added to either surface is carried without being used.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.5. `DeadSurfaceTests` already refuses an internal member of
/// the terminal that no other file names. Nothing did the same for what 006 added elsewhere: the
/// console's bound properties, the shared library's constants, and the enum values a switch has to
/// handle.
/// </para>
/// <para>
/// An unused bound property on a page is worse than dead code, because it is part of that page's
/// URL contract: a reader seeing it assumes the page answers to it.
/// </para>
/// </remarks>
public sealed class ConsolePageSurfaceTests
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
    [InlineData("ShowArchived")]
    [InlineData("Sort")]
    [InlineData("Hidden")]
    [InlineData("Typed")]
    [InlineData("Describes")]
    public void Every_property_006_added_to_a_page_is_read_by_its_markup_or_its_model(string added)
    {
        // A bound property nothing renders is part of a URL contract nobody honours. Checked
        // against the whole console tree, markup included, because a Razor page reads its model
        // from the .cshtml and nowhere else.
        var console = Sources("src", "Parley.Web");
        var uses = console.Split(added, StringSplitOptions.None).Length - 1;

        // Declared once, and used at least once more.
        Assert.True(uses >= 2, $"'{added}' appears {uses} time(s) in the console");
    }

    [Fact]
    public void Every_value_of_the_two_new_enums_is_handled_somewhere()
    {
        // An enum value nothing switches on is a state the surfaces cannot be in, or one they will
        // fall through on. Both are worth knowing about before somebody adds a third.
        var shared = Sources("src", "Parley.Client");
        var terminal = Sources("src", "Parley.Terminal");
        var console = Sources("src", "Parley.Web");
        var everything = shared + terminal + console;

        foreach (var value in Enum.GetNames<Parley.Client.Views.Viewing>())
        {
            Assert.Contains($"Viewing.{value}", everything, StringComparison.Ordinal);
        }

        foreach (var value in Enum.GetNames<Parley.Client.Views.SortBy>())
        {
            Assert.Contains($"SortBy.{value}", everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_line_kind_the_renderer_can_produce_is_one_something_reads()
    {
        // The transcript's line kinds decide how a surface draws a line. One nothing reads is a
        // distinction being computed and thrown away.
        var shared = Sources("src", "Parley.Client");
        var surfaces = Sources("src", "Parley.Terminal") + Sources("src", "Parley.Web");

        foreach (var value in Enum.GetNames<Parley.Client.Rendering.LineKind>())
        {
            Assert.Contains($"LineKind.{value}", shared + surfaces, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_check_is_reading_both_trees()
    {
        Assert.Contains("BindProperty", Sources("src", "Parley.Web"), StringComparison.Ordinal);
        Assert.Contains("ViewPlace", Sources("src", "Parley.Terminal"), StringComparison.Ordinal);
    }
}
