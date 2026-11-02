using System.Text;
using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a file larger than <see cref="StrictJson.MostBytes"/> is refused as unreadable, in fixed words and without
/// being read, wherever a repository's or a node's JSON is read.
/// </summary>
/// <remarks>
/// Lens 2 run 139: a repository's <c>.mcp.json</c> past about a billion characters threw
/// <see cref="OutOfMemoryException"/> out of the read, past every catch, through the node's catalogue scan, and ended
/// the node with every resident agent on the machine. The stream cases, which touch no file, are
/// <see cref="AnOversizedStreamIsRefusedUnreadTests"/>.
/// </remarks>
[Integration]
public sealed class AnOversizedFileIsRefusedUnreadTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-oversized-" + Guid.NewGuid().ToString("N"));

    private readonly string _elsewhere =
        Path.Combine(Path.GetTempPath(), "parley-oversized-elsewhere-" + Guid.NewGuid().ToString("N"));

    public AnOversizedFileIsRefusedUnreadTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_elsewhere);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            // A link is deleted as itself, before anything recursive runs.
            File.Delete(Path.Combine(_workspace, "CLAUDE.md"));
            Directory.Delete(_workspace, recursive: true);
            Directory.Delete(_elsewhere, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_file_one_byte_over_the_bound_is_refused_in_fixed_words()
    {
        var path = Sized("over.json", StrictJson.MostBytes + 1);

        var refused = Assert.Throws<JsonException>(() => StrictJson.ReadFile(path));

        Assert.Equal(StrictJson.TooLargeMessage, refused.Message);
    }

    [Fact]
    public async Task A_file_one_byte_over_the_bound_is_refused_when_read_asynchronously()
    {
        var path = Sized("over.json", StrictJson.MostBytes + 1);

        var refused = await Assert.ThrowsAsync<JsonException>(() => StrictJson.ReadFileAsync(path, Ct));

        Assert.Equal(StrictJson.TooLargeMessage, refused.Message);
    }

    [Fact]
    public void A_file_at_the_bound_is_read_whole_and_decoded_as_before()
    {
        var path = Sized("at.json", StrictJson.MostBytes);

        Assert.Equal(File.ReadAllText(path), StrictJson.ReadFile(path));

        var marked = Path.Combine(_workspace, "marked.json");
        File.WriteAllText(marked, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{}", StrictJson.ReadFile(marked));
    }

    [Fact]
    public void An_oversized_launch_configuration_is_reported_and_not_read()
    {
        Sized(LaunchConfiguration.FileName, StrictJson.MostBytes + 1);

        Assert.False(LaunchConfiguration.TryRead(_workspace, out _, out var problem));
        Assert.Equal(StrictJson.TooLargeMessage, problem);
    }

    [Fact]
    public async Task An_oversized_launch_configuration_is_an_answer_to_the_broker_not_a_throw()
    {
        Sized(LaunchConfiguration.FileName, StrictJson.MostBytes + 1);

        var said = await OwnershipLine.WriteAsync(
            _workspace, null, AgentName.Create("orders@test"), "Owns invoices.", Ct);

        Assert.NotNull(said);
        Assert.Contains(StrictJson.TooLargeMessage, said, StringComparison.Ordinal);
        Assert.Contains("Nothing has been written.", said, StringComparison.Ordinal);
    }

    // Lens 3 run 147: the instructions file is prose, read whole, and a long one stopped the enrolment that only appends
    // to it, with a refusal that blamed .mcp.json.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_instructions_file_of_any_size_enrols_as_before(bool describe)
    {
        var instructions = Path.Combine(_workspace, "CLAUDE.md");
        var prose = "# Orders\n\n" + new string('x', StrictJson.MostBytes) + "\n";
        File.WriteAllText(instructions, prose, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (code, _, error) = await EnrolAsync(describe);

        Assert.Equal(string.Empty, error);
        Assert.Equal(0, code);

        var after = File.ReadAllText(instructions);

        Assert.StartsWith(prose, after, StringComparison.Ordinal);
        Assert.Equal(describe, after.Length == prose.Length);
    }

    [Fact]
    public async Task An_instructions_file_of_any_size_is_appended_to_by_the_terminal_s_enrolment()
    {
        var instructions = Path.Combine(_workspace, "CLAUDE.md");
        var prose = "# Orders\n\n" + new string('x', StrictJson.MostBytes) + "\n";
        File.WriteAllText(instructions, prose, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = await Enrolment.AddAsync(
            new EnrollmentEntry(_workspace, "orders", "Orders things.", Unattended: true) { Enroll = true },
            "test",
            Path.Combine(_workspace, "node.json"),
            instructions,
            Ct);

        Assert.Contains(instructions, outcome.Written);
        Assert.StartsWith(prose, File.ReadAllText(instructions), StringComparison.Ordinal);
    }

    // Lens 3 run 148: a linked instructions file is declined, and its text is read for the plan's sake; read as JSON is
    // read, a long one stopped the enrolment with a refusal that blamed .mcp.json.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_instructions_file_linked_to_a_long_file_on_this_machine_is_left_alone(bool describe)
    {
        var target = Path.Combine(_elsewhere, "CLAUDE.md");
        var prose = "# Orders\n\n" + new string('x', StrictJson.MostBytes) + "\n";
        File.WriteAllText(target, prose, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var instructions = Path.Combine(_workspace, "CLAUDE.md");

        try
        {
            _ = File.CreateSymbolicLink(instructions, target);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let this user make a symbolic link (Windows without Developer Mode).");
        }

        var (code, output, error) = await EnrolAsync(describe);

        Assert.Equal(string.Empty, error);
        Assert.Equal(0, code);
        Assert.Contains($"{instructions}: left alone, ", output, StringComparison.Ordinal);
        Assert.Equal(prose, File.ReadAllText(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oversized_launch_configuration_is_named_in_one_sentence(bool describe)
    {
        File.WriteAllText(Path.Combine(_workspace, "CLAUDE.md"), "# Orders\n");
        Sized(LaunchConfiguration.FileName, StrictJson.MostBytes + 1);

        var (code, _, error) = await EnrolAsync(describe);

        Assert.Equal(78, code);
        Assert.Contains(
            $"{Path.Combine(_workspace, LaunchConfiguration.FileName)} cannot be merged into",
            error,
            StringComparison.Ordinal);
        Assert.Contains(StrictJson.TooLargeMessage + "\n", error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("CLAUDE.md", error, StringComparison.Ordinal);
        Assert.DoesNotContain("..", error, StringComparison.Ordinal);
    }

    // The enrolment of this one repository, attended, as a surface hands it over.
    private async Task<(int Code, string Out, string Error)> EnrolAsync(bool describe)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.EnrolAsync(
            [new EnrollmentEntry(_workspace, "orders", "Orders things.", Unattended: false) { Enroll = true }],
            describe,
            new Resolved(Path.Combine(_workspace, "node.json"), ValueSource.Argument),
            output,
            error,
            cancellationToken: Ct);

        return (code, output.ToString(), error.ToString());
    }

    // A JSON object padded with spaces to exactly the size asked for.
    private string Sized(string name, int bytes)
    {
        var path = Path.Combine(_workspace, name);
        var text = new StringBuilder(bytes).Append('{').Append(' ', bytes - 2).Append('}').ToString();

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }
}
