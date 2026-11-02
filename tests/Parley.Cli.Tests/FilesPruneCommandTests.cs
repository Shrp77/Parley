using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files prune</c>: every file in the location goes, behind the project's name typed back (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// The confirmation is the project's name because that is what the other surfaces take for an act that cannot be
/// undone across a whole project, and a <c>yes</c> here would be the one door in the deployment easier to walk through
/// by habit.
/// </remarks>
[Integration]
public sealed class FilesPruneCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task Typing_the_projects_name_removes_every_file_in_every_conversation()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "refunds.md", "within thirty days", "returns");
        _files.ProjectName = "alpha";
        _files.Input = "alpha\n";

        var (code, output, _) = await _files.RunAsync("prune");

        Assert.Equal(0, code);
        Assert.Contains("2 file(s)", output, StringComparison.Ordinal);
        Assert.Contains("Type the project's name", output, StringComparison.Ordinal);
        Assert.Contains("Removed 2 file(s), freeing 28 B.", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task Anything_but_the_projects_name_removes_nothing()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.ProjectName = "alpha";
        _files.Input = "yes\n";

        var (code, output, _) = await _files.RunAsync("prune");

        Assert.Equal(1, code);
        Assert.Contains("Nothing was removed.", output, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task With_no_project_named_a_phrase_nobody_types_by_habit_is_asked_for()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Input = "remove every file\n";

        var (code, output, _) = await _files.RunAsync("prune");

        Assert.Equal(0, code);
        Assert.Contains("Type 'remove every file' to confirm", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task The_long_flag_removes_without_asking()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.ProjectName = "alpha";

        var (code, output, _) = await _files.RunAsync("prune", "--yes-remove-every-file");

        Assert.Equal(0, code);
        Assert.DoesNotContain("Type", output, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    [Fact]
    public async Task An_empty_location_has_nothing_to_prune_and_asks_nothing()
    {
        var (code, output, _) = await _files.RunAsync("prune");

        Assert.Equal(0, code);
        Assert.Contains("There is nothing to prune.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prune_is_the_whole_location_and_refuses_to_be_narrowed_to_one_conversation()
    {
        // Removing one conversation's files is `remove`, which names each. A prune that took a conversation would be a
        // second way to do that behind a weaker description.
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, _, error) = await _files.RunAsync("prune", "--conversation", "pricing", "--yes-remove-every-file");

        Assert.Equal(64, code);
        Assert.Contains("Usage: parley files", error, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }
}
