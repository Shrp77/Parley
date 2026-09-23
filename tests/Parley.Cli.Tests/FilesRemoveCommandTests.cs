using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files remove</c>: the person removes named files, whoever placed them, after being told what goes
/// (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// Each refusal is checked by what is still in the location afterwards, not only by what was printed: a command that
/// said "Nothing was removed." and removed something is the failure that matters.
/// </remarks>
public sealed class FilesRemoveCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task It_describes_each_file_asks_and_yes_removes_a_file_an_agent_placed()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Input = "yes\n";

        var (code, output, _) = await _files.RunAsync("remove", "notes.md");

        Assert.Equal(0, code);
        Assert.Contains("whichever agent placed them", output, StringComparison.Ordinal);
        Assert.Contains("pricing", output, StringComparison.Ordinal);
        Assert.Contains("Type yes to remove them", output, StringComparison.Ordinal);
        Assert.Contains("Removed pricing/notes.md.", output, StringComparison.Ordinal);
        Assert.Contains("Freed 10 B.", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task Anything_but_yes_removes_nothing()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Input = "y\n";

        var (code, output, _) = await _files.RunAsync("remove", "notes.md");

        Assert.Equal(1, code);
        Assert.Contains("Nothing was removed.", output, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task Yes_removes_without_asking()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, output, _) = await _files.RunAsync("remove", "notes.md", "--yes");

        Assert.Equal(0, code);
        Assert.DoesNotContain("Type yes", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task A_name_not_in_the_location_is_reported_and_the_rest_are_still_removed()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, output, error) = await _files.RunAsync("remove", "notes.md", "missing.md", "--yes");

        Assert.Equal(1, code);
        Assert.Contains("Removed pricing/notes.md.", output, StringComparison.Ordinal);
        Assert.Contains("Not removed: missing.md.", error, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task A_name_held_by_two_conversations_is_refused_until_one_is_named()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "notes.md", "within thirty days", "returns");

        var (refusedCode, _, error) = await _files.RunAsync("remove", "notes.md", "--yes");

        Assert.Equal(1, refusedCode);
        Assert.Contains("Not removed: notes.md.", error, StringComparison.Ordinal);
        Assert.Equal(2, await _files.CountAsync());

        var (code, output, _) = await _files.RunAsync("remove", "notes.md", "--conversation", "returns", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("Removed returns/notes.md.", output, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task More_than_two_hundred_names_are_refused_before_anything_is_asked_or_removed()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Input = "yes\n";

        var names = new[] { "remove", "notes.md" }.Concat(Enumerable.Range(0, 200).Select(i => "other" + i + ".md")).ToArray();

        var (code, output, error) = await _files.RunAsync(names);

        Assert.Equal(64, code);
        Assert.Contains("At most 200 files", error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task The_tool_itself_takes_yes_and_a_conversation()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, output, error) = ToolProcess.Reading(
            "--broker", _files.Broker.Address, "files", "remove", "notes.md", "--conversation", "pricing", "--yes");

        Assert.True(code == 0, error);
        Assert.Contains("Removed pricing/notes.md.", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }
}
