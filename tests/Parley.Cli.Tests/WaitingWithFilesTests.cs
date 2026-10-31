using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley waiting --with-files</c>: the third count, asked for rather than always printed (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// The plugin's waiting hook is the reader. A hook installed before files existed splits the line on its first space,
/// so the plain call must keep printing two counts; and the flag must reach the command, which the tool's refusal of
/// unknown options stopped it doing the first time it was written.
/// </remarks>
[Integration]
public sealed class WaitingWithFilesTests : IAsyncDisposable
{
    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task The_flag_adds_a_third_count_and_the_plain_call_keeps_two()
    {
        await _files.PlaceAsync(FileLocationFixture.Billing, "notes.md", "net of tax", "pricing");

        var (withCode, withFiles, withError) = ToolProcess.Reading(
            "--broker", _files.Broker.Address, "waiting", FileLocationFixture.Billing, "--with-files");

        Assert.True(withCode == 0, withError);
        Assert.Equal("0 0 0", withFiles.Trim());

        var (plainCode, plain, plainError) = ToolProcess.Reading(
            "--broker", _files.Broker.Address, "waiting", FileLocationFixture.Billing);

        Assert.True(plainCode == 0, plainError);
        Assert.Equal("0 0", plain.Trim());
    }

    [Fact]
    public void The_flag_is_refused_on_a_command_with_no_count_to_add_it_to()
    {
        var (code, _, error) = ToolProcess.Reading("--broker", _files.Broker.Address, "open", "--with-files");

        Assert.Equal(64, code);
        Assert.Contains("--with-files is only for `parley waiting`", error, StringComparison.Ordinal);
    }
}
