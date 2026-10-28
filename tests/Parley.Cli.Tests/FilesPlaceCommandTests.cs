using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files place</c>: the person placing a file from this machine, against a real broker (SPEC.md §10.2
/// <c>place_file</c>, §9.5).
/// </summary>
/// <remarks>
/// Each case reads back what the broker lists afterwards, because a command that said "Placed" over a placement the
/// broker refused is the failure that matters.
/// </remarks>
public sealed class FilesPlaceCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-place-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _files.DisposeAsync();

        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_file_is_placed_as_the_persons_in_the_conversation_named()
    {
        await _files.PlaceAsync(Billing, "rates.md", "twenty percent", "pricing");
        var path = Write("notes.md", "net of tax");

        var (code, output, error) = await _files.RunAsync("place", path, "--conversation", "pricing");

        Assert.True(code == 0, error);
        Assert.Contains(
            "Placed notes.md in pricing: 10 B, sha256 " + ContentHash.Of("net of tax"u8).Short + ".",
            output,
            StringComparison.Ordinal);

        var placed = await PlacedAsync("notes.md");
        Assert.Null(placed.PlacedBy);
        Assert.Equal("pricing", placed.ConversationId);
    }

    [Fact]
    public async Task A_name_given_is_the_name_placed_and_one_renamed_for_its_characters_says_so()
    {
        await _files.PlaceAsync(Billing, "rates.md", "x", "pricing");
        var path = Write("draft.md", "net of tax");

        var (code, output, _) = await _files.RunAsync("place", path, "--conversation", "pricing", "--name", "final notes.md");

        Assert.Equal(0, code);
        Assert.Contains("Placed final_notes.md in pricing", output, StringComparison.Ordinal);
        Assert.Contains("It was asked for as 'final notes.md'", output, StringComparison.Ordinal);
        Assert.NotNull(await PlacedAsync("final_notes.md"));
    }

    [Fact]
    public async Task The_same_content_again_is_already_there_and_other_content_under_the_name_is_refused()
    {
        await _files.PlaceAsync(Billing, "rates.md", "x", "pricing");
        var path = Write("notes.md", "net of tax");

        Assert.Equal(0, (await _files.RunAsync("place", path, "--conversation", "pricing")).Code);

        var (again, said, _) = await _files.RunAsync("place", path, "--conversation", "pricing");

        Assert.Equal(0, again);
        Assert.Contains("is already in pricing with exactly this content", said, StringComparison.Ordinal);

        await File.WriteAllTextAsync(path, "gross of tax", Ct);

        var (code, output, error) = await _files.RunAsync("place", path, "--conversation", "pricing");

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, output);
        Assert.StartsWith("Not placed:", error, StringComparison.Ordinal);
        Assert.Equal(ContentHash.Of("net of tax"u8), (await PlacedAsync("notes.md")).Sha256);
    }

    [Fact]
    public async Task A_name_an_agent_placed_is_refused()
    {
        await _files.PlaceAsync(Billing, "notes.md", "theirs", "pricing");
        var path = Write("notes.md", "mine");

        var (code, _, error) = await _files.RunAsync("place", path, "--conversation", "pricing");

        Assert.Equal(1, code);
        Assert.StartsWith("Not placed:", error, StringComparison.Ordinal);
        Assert.Equal(AgentName.Create(Billing), (await PlacedAsync("notes.md")).PlacedBy);
    }

    [Fact]
    public async Task A_path_with_no_file_is_refused_and_nothing_is_placed()
    {
        await _files.PlaceAsync(Billing, "rates.md", "x", "pricing");

        var (code, _, error) = await _files.RunAsync(
            "place", Path.Combine(_directory.FullName, "absent.md"), "--conversation", "pricing");

        Assert.Equal(1, code);
        Assert.Contains("There is no file at", error, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_md_or_txt_is_refused()
    {
        await _files.PlaceAsync(Billing, "rates.md", "x", "pricing");
        var path = Write("run.bat", "echo");

        var (code, _, error) = await _files.RunAsync("place", path, "--conversation", "pricing");

        Assert.Equal(1, code);
        Assert.StartsWith("Not placed:", error, StringComparison.Ordinal);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_is_refused()
    {
        var path = Write("notes.md", "net of tax");

        var (code, _, error) = await _files.RunAsync("place", path, "--conversation", "nowhere");

        Assert.Equal(1, code);
        Assert.StartsWith("Not placed:", error, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory.FullName, name);

        File.WriteAllText(path, text);

        return path;
    }

    private async Task<SharedFile> PlacedAsync(string name)
    {
        var listing = await _files.Client.ListSharedFilesAsync(null, FileLimits.MaximumPageSize, null, Ct);

        return Assert.Single(listing.Page.Files, f => f.Name.Value == name);
    }
}
