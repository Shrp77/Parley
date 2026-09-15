using System.Globalization;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files list</c>: what is in the location, who placed it, and how much each agent has placed (SPEC.md §9.5,
/// §10.2).
/// </summary>
/// <remarks>
/// Every name here is somebody else's text on its way to a terminal, so one case places a name built to move the cursor
/// and checks it arrives as a single row.
/// </remarks>
public sealed class FilesListCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task An_empty_location_says_so()
    {
        var (code, output, _) = await _files.RunAsync("list");

        Assert.Equal(0, code);
        Assert.Contains("The file location is empty.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_file_is_a_row_under_its_conversation_with_its_size_short_hash_and_placer_and_totals_follow()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "rates.txt", "twenty percent", "pricing");

        var (code, output, _) = await _files.RunAsync("list");
        var hash = ContentHash.Of("net of tax"u8);

        Assert.Equal(0, code);
        Assert.Contains("pricing  (2 file(s), 24 B)", output, StringComparison.Ordinal);

        var row = Assert.Single(Lines(output), l => l.Contains("notes.md", StringComparison.Ordinal));
        Assert.Contains("10 B", row, StringComparison.Ordinal);
        Assert.Contains(hash.Short, row, StringComparison.Ordinal);
        Assert.Contains(Billing, row, StringComparison.Ordinal);
        Assert.DoesNotContain(hash.Value, output, StringComparison.Ordinal);

        Assert.Contains("2 file(s), 24 B in all.", output, StringComparison.Ordinal);

        // Review gate, lens 3 run 3.3: the rows' times were UTC with no zone, beside a tool whose every other time is local.
        Assert.Contains(Parley.Client.Rendering.LocalTime.Note(), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placements_and_their_bytes_are_given_for_each_agent_under_the_list()
    {
        // FR-039: counted so a limit can be set from evidence, and shown to the person because they would set it.
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "notes.md", "gross of tax", "pricing");

        var (_, output, _) = await _files.RunAsync("list");

        var lines = Lines(output);
        var heading = lines.IndexOf("Placements:");

        Assert.True(heading >= 0, output);
        Assert.Contains(lines.Skip(heading + 1), l => l.Contains(Billing, StringComparison.Ordinal)
                                                     && l.Contains("2 placement(s), 22 B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_prints_the_whole_hash()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (_, output, _) = await _files.RunAsync("list", "--full");

        Assert.Contains(ContentHash.Of("net of tax"u8).Value, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_conversation_lists_only_its_own_files()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "refunds.md", "within thirty days", "returns");

        var (_, output, _) = await _files.RunAsync("list", "--conversation", "returns");

        Assert.Contains("refunds.md", output, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_location_larger_than_a_page_is_printed_whole_and_each_file_once()
    {
        // The broker answers at most two hundred files a call, so this is the case where a report that read one page
        // would look complete and be missing fifty.
        for (var i = 0; i < 250; i++)
        {
            await _files.PlaceAsync(Billing, "f" + i.ToString("000", CultureInfo.InvariantCulture) + ".md", "x", "pricing");
        }

        var (_, output, _) = await _files.RunAsync("list");
        var rows = Lines(output).Where(l => l.StartsWith("  f", StringComparison.Ordinal)).ToList();

        Assert.Equal(250, rows.Count);
        Assert.Equal(250, rows.Select(r => r.Trim().Split(' ')[0]).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("250 file(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_built_to_move_the_terminal_arrives_as_one_row()
    {
        // Both are legal in a name: a C1 next-line, which some terminals take as a line break, and a right-to-left
        // override, which makes a name read as something it is not.
        var nextLine = "notes" + (char)0x85 + "rates.md";
        var reversed = "invoice" + (char)0x202E + "dm.txt";

        await _files.PlaceAsync(Billing, nextLine, "a", "pricing");
        await _files.PlaceAsync(Billing, reversed, "b", "pricing");

        var (_, output, _) = await _files.RunAsync("list");

        Assert.DoesNotContain((char)0x85, output);
        Assert.DoesNotContain((char)0x202E, output);
        Assert.Contains("notes?rates.md", output, StringComparison.Ordinal);
        Assert.Contains("invoice?dm.txt", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_accepts_the_options_files_reads_and_still_refuses_them_elsewhere()
    {
        // The tool refuses any argument still starting with a hyphen before a command runs. The first version of this
        // command was reachable only in process: every option it reads was refused on the way to it.
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, output, error) = ToolProcess.Reading(
            "--broker", _files.Broker.Address, "files", "list", "--full", "--conversation", "pricing");

        Assert.True(code == 0, error);
        Assert.Contains(ContentHash.Of("net of tax"u8).Value, output, StringComparison.Ordinal);

        var (elsewhere, _, refused) = ToolProcess.Reading("--broker", _files.Broker.Address, "open", "--full");

        Assert.NotEqual(0, elsewhere);
        Assert.Contains("'--full' is not an option", refused, StringComparison.Ordinal);
    }

    private static List<string> Lines(string output) =>
        [.. output.Split('\n').Select(l => l.TrimEnd('\r'))];
}
