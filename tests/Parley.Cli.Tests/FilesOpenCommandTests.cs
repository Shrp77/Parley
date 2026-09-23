using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files open</c>: a placed file opened in place, on the machine that holds it and on no other (SPEC.md §10.2,
/// §13).
/// </summary>
/// <remarks>
/// Through the launcher's seam throughout, which records instead of launching; the refusals are checked by what was
/// launched, which must be nothing.
/// </remarks>
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
    public async Task A_file_on_another_machine_is_described_and_nothing_is_launched()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Machine = "elsewhere";

        var (_, output, _) = await _files.RunAsync("open", "notes.md");

        Assert.Empty(_files.Launched);
        Assert.Contains("is on machine " + TestMachine.Name + ", at", output, StringComparison.Ordinal);
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
