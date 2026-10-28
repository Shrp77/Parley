using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files problems</c> and <c>parley files dismiss</c>: what sweeps found, and the person taking one off the list
/// (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// The problem is made the way a real one is: a placed file's bytes are changed on disk and the location is swept. Every
/// path printed is somebody else's text, so one case raises a problem whose path is built to move the cursor.
/// </remarks>
public sealed class FilesProblemsCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task With_nothing_found_it_says_none_are_waiting_and_where_the_quarantine_directory_is()
    {
        var (code, output, _) = await _files.RunAsync("problems");

        Assert.Equal(0, code);
        Assert.Contains("No file problems are waiting.", output, StringComparison.Ordinal);
        Assert.Contains(_files.Broker.Runtime.Files.Quarantine, output, StringComparison.Ordinal);
        Assert.DoesNotContain("parley files dismiss", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_file_is_listed_with_both_hashes_and_where_it_went()
    {
        await _files.PlaceAsync(Billing, "terms.md", "net 30", "pricing");
        await _files.TamperAsync("pricing", "terms.md", "net 99");

        var (code, output, _) = await _files.RunAsync("problems");

        Assert.Equal(0, code);
        Assert.Contains("1 file problem(s) waiting.", output, StringComparison.Ordinal);
        Assert.Contains("Changed on disk, moved to quarantine: pricing/terms.md", output, StringComparison.Ordinal);
        Assert.Contains("Conversation: pricing", output, StringComparison.Ordinal);
        Assert.Contains("sha256 placed: " + ContentHash.Of("net 30"u8).Value, output, StringComparison.Ordinal);
        Assert.Contains("sha256 found:  " + ContentHash.Of("net 99"u8).Value, output, StringComparison.Ordinal);
        Assert.Contains("Moved to: ", output, StringComparison.Ordinal);
        Assert.Contains("parley files dismiss <id>", output, StringComparison.Ordinal);

        // Each fact sits under its number, indented, so nothing from the problem starts a line.
        Assert.All(
            Lines(output).Where(l => l.Contains("terms.md", StringComparison.Ordinal)),
            l => Assert.StartsWith("  ", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dismissing_takes_it_off_the_list_deletes_nothing_and_all_shows_it_again()
    {
        await _files.PlaceAsync(Billing, "terms.md", "net 30", "pricing");
        await _files.TamperAsync("pricing", "terms.md", "net 99");

        var id = Assert.Single((await _files.Client.ListFileProblemsAsync(cancellationToken: TestContext.Current.CancellationToken)).Problems).Id;
        var quarantined = Directory.EnumerateFiles(_files.Broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList();

        var (code, output, _) = await _files.RunAsync("dismiss", "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(0, code);
        Assert.Contains("Dismissed file problem #" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
        Assert.Equal(quarantined, Directory.EnumerateFiles(_files.Broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList());

        var (_, waiting, _) = await _files.RunAsync("problems");
        Assert.Contains("No file problems are waiting.", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("terms.md", waiting, StringComparison.Ordinal);

        var (_, all, _) = await _files.RunAsync("problems", "--all");
        Assert.Contains("(dismissed)", all, StringComparison.Ordinal);
        Assert.Contains("pricing/terms.md", all, StringComparison.Ordinal);

        var (again, _, error) = await _files.RunAsync("dismiss", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, again);
        Assert.Contains("is waiting. Nothing was changed.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_reaches_the_command_through_the_real_binary()
    {
        // `--all` is also enrolment's, and the tool takes it centrally for that. Here it must arrive.
        await _files.PlaceAsync(Billing, "terms.md", "net 30", "pricing");
        await _files.TamperAsync("pricing", "terms.md", "net 99");
        var id = Assert.Single((await _files.Client.ListFileProblemsAsync(cancellationToken: TestContext.Current.CancellationToken)).Problems).Id;
        Assert.True(await _files.Client.DismissFileProblemAsync(id, TestContext.Current.CancellationToken));

        var (code, output, error) = ToolProcess.Reading("files", "problems", "--all", "--broker", _files.Broker.Address);

        Assert.True(code == 0, error);
        Assert.Contains("(dismissed)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_that_is_not_one_is_refused_as_usage()
    {
        var (code, _, error) = await _files.RunAsync("dismiss", "terms.md");

        Assert.Equal(64, code);
        Assert.Contains("is not a problem's number", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_built_to_move_the_cursor_arrives_as_one_inert_line()
    {
        var hostile = "pricing/notes" + (char)0x1B + "[2J" + (char)0x0A + "#99  forged.md";

        await _files.RaiseHeldAsync(hostile, "pricing");

        var (_, output, _) = await _files.RunAsync("problems");

        Assert.DoesNotContain((char)0x1B, output);
        Assert.DoesNotContain(Lines(output), l => l.StartsWith("#99", StringComparison.Ordinal));
        Assert.Contains("Could not be moved or deleted: pricing/notes?[2J?#99  forged.md", output, StringComparison.Ordinal);
    }

    private static List<string> Lines(string output) => [.. output.ReplaceLineEndings("\n").Split('\n')];
}
