using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Every way of mistyping <c>parley files</c> is refused as usage, through the real binary.
/// </summary>
/// <remarks>
/// An option a subcommand has no use for must not be parsed and ignored, or
/// <c>list --yes</c> would succeed and <c>prune --yes</c> would ask anyway. The tool's own rule is that a flag accepted and ignored
/// is worse than one refused, because whoever typed it believes it did something.
/// </remarks>
public sealed class FilesCommandUsageTests : IAsyncDisposable
{
    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task Every_mistyped_files_command_is_refused_rather_than_done_or_ignored()
    {
        await _files.PlaceAsync(FileLocationFixture.Billing, "notes.md", "net of tax", "pricing");

        string[][] mistyped =
        [
            ["files"],
            ["files", "bogus"],
            ["files", "list", "notes.md"],
            ["files", "remove"],
            ["files", "open"],
            ["files", "prune", "--conversation", "pricing"],
            ["files", "list", "--conversation"],
            ["files", "list", "--yes"],
            ["files", "list", "--yes-remove-every-file"],
            ["files", "open", "notes.md", "--full"],
            ["files", "open", "notes.md", "--yes"],
            ["files", "folder", "--yes-remove-every-file"],
            ["files", "folder", "--full"],
            ["files", "prune", "--yes"],
            ["files", "prune", "--full"],
            ["files", "remove", "notes.md", "--full"],
            ["files", "remove", "notes.md", "--yes-remove-every-file"],
            ["files", "place", "notes.md"],
            ["files", "place", "--conversation", "pricing"],
            ["files", "place", "a.md", "b.md", "--conversation", "pricing"],
            ["files", "place", "notes.md", "--conversation", "pricing", "--yes"],
            ["files", "list", "--name", "notes.md"],
            ["files", "list", "--all"],
            ["files", "problems", "notes.md"],
            ["files", "problems", "--yes"],
            ["files", "problems", "--conversation", "pricing"],
            ["files", "dismiss"],
            ["files", "dismiss", "1", "2"],
            ["files", "dismiss", "1", "--all"],
            ["files", "dismiss", "one"],
            ["files", "prune", "--all"],
        ];

        var accepted = new List<string>();

        foreach (var arguments in mistyped)
        {
            var (code, _, _) = ToolProcess.Reading([.. arguments, "--broker", _files.Broker.Address]);

            if (code != 64)
            {
                accepted.Add($"'{string.Join(' ', arguments)}' exited {code}");
            }
        }

        Assert.Equal(1, await _files.CountAsync());
        Assert.True(accepted.Count == 0, string.Join('\n', accepted));
    }

    [Fact]
    public void An_option_refused_is_refused_before_the_broker_is_asked_and_names_only_the_option()
    {
        // The refusal is decided from the command line alone — a broker nothing listens on
        // is never reached, so the answer is usage rather than unreachable — and it repeats the option's own spelling,
        // never a name typed beside it, which is somebody's text on its way to a terminal.
        var hostile = "notes" + (char)0x1B + "]0;owned" + (char)0x07 + ".md";

        var (code, output, error) = ToolProcess.Reading("files", "open", hostile, "--full", "--broker", Loopback.NothingListens());

        Assert.Equal(64, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("'--full' is not an option `parley files open` takes.", error, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x1B, error);
        Assert.DoesNotContain("owned", error, StringComparison.Ordinal);
    }
}
