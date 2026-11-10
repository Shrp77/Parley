using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files open</c>: a placed file opened in place on the machine that holds it, and as a checked copy read
/// through the broker anywhere else (SPEC.md §10.2, §13).
/// </summary>
/// <remarks>
/// Through the launcher's seam throughout, which records instead of launching; the refusals are checked by what was
/// launched, which must be nothing.
/// </remarks>
[Integration]
public sealed class FilesOpenCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task A_file_on_this_machine_is_opened_where_it_is()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, output, _) = await _files.RunAsync("open", "notes.md");

        Assert.Equal(0, code);

        var (path, folder) = Assert.Single(_files.Launched);
        Assert.False(folder);
        Assert.Equal("notes.md", Path.GetFileName(path));
        Assert.Equal("net of tax", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("Opened", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_on_another_machine_opens_a_checked_copy_read_through_the_broker()
    {
        // At the end of a tunnel the broker's disk is not this one, so the file is read and a copy opened, as the
        // terminal and the web console do (SPEC.md §10.2).
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Machine = "elsewhere";

        var (_, output, _) = await _files.RunAsync("open", "notes.md", "--conversation", "pricing");

        var (path, folder) = Assert.Single(_files.Launched);
        Assert.False(folder);
        Assert.StartsWith(_files.Copies, path, StringComparison.Ordinal);
        Assert.Equal("net of tax", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("Opened a copy of notes.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_on_another_machine_with_no_conversation_named_is_found_by_its_name()
    {
        // One file of the name in the whole location is the one meant, as the broker's lookup already said.
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Machine = "elsewhere";

        var (code, output, _) = await _files.RunAsync("open", "notes.md");

        Assert.Equal(0, code);
        var (path, _) = Assert.Single(_files.Launched);
        Assert.Equal("net of tax", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("Opened a copy of notes.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_open_refused_launches_nothing_and_exits_one()
    {
        // An image whose leading bytes are not its format is never launched (0.58.1); the command said so and exited 0
        // with no test to notice (review, lens 3 round 29).
        await _files.PlaceAsync(Billing, "diagram.png", "not an image", "pricing");

        var (code, output, _) = await _files.RunAsync("open", "diagram.png");

        Assert.Equal(1, code);
        Assert.Empty(_files.Launched);
        Assert.Contains("diagram.png", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_not_in_the_location_launches_nothing()
    {
        var (code, _, error) = await _files.RunAsync("open", "missing.md");

        Assert.Equal(1, code);
        Assert.Contains("missing.md:", error, StringComparison.Ordinal);
        Assert.Empty(_files.Launched);
    }

    [Fact]
    public async Task A_name_held_by_two_conversations_launches_nothing_until_one_is_named()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "notes.md", "within thirty days", "returns");

        var (refused, _, _) = await _files.RunAsync("open", "notes.md");

        Assert.Equal(1, refused);
        Assert.Empty(_files.Launched);

        var (code, _, _) = await _files.RunAsync("open", "notes.md", "--conversation", "returns");

        Assert.Equal(0, code);
        Assert.Equal(
            "within thirty days",
            await File.ReadAllTextAsync(Assert.Single(_files.Launched).Path, TestContext.Current.CancellationToken));
    }
}
