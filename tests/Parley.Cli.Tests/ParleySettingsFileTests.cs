using System.Text;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>Parley's settings for the whole machine: <c>&lt;root&gt;/settings.json</c> (spec 036, research R8).</summary>
/// <remarks>
/// <b>The owner, 2026-11-06:</b> how far back the Inbox's Closed view goes is set in Parley's settings, and the terminal
/// reads it too. A surface must open whatever the file holds, and a write must lose nothing else in it.
/// </remarks>
[Integration]
public sealed class ParleySettingsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-settings-" + Path.GetRandomFileName());

    public ParleySettingsFileTests() => Directory.CreateDirectory(_root);

    private string File => Path.Combine(_root, ParleySettings.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void With_no_file_the_window_is_seven_days()
    {
        Assert.Equal(7, ParleySettings.ReadClosedDays(_root));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"inbox":"seven"}""")]
    [InlineData("""{"inbox":{"closedDays":"30"}}""")]
    [InlineData("""{"inbox":{"closedDays":0}}""")]
    [InlineData("""{"inbox":{"closedDays":366}}""")]
    [InlineData("""{"inbox":{"closedDays":2.5}}""")]
    [InlineData("""{"inbox":{"closedDays":3},"inbox":{"closedDays":4}}""")]
    public void Anything_but_a_whole_number_in_range_reads_as_the_default(string contents)
    {
        System.IO.File.WriteAllText(File, contents);

        Assert.Equal(7, ParleySettings.ReadClosedDays(_root));
    }

    [Fact]
    public void A_window_written_is_read_back()
    {
        Assert.Null(ParleySettings.WriteClosedDays(_root, 30));
        Assert.Equal(30, ParleySettings.ReadClosedDays(_root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void A_window_out_of_range_is_refused_and_the_file_is_left_alone(int days)
    {
        Assert.Null(ParleySettings.WriteClosedDays(_root, 10));

        var refused = ParleySettings.WriteClosedDays(_root, days);

        Assert.Contains("from 1 to 365", refused, StringComparison.Ordinal);
        Assert.Equal(10, ParleySettings.ReadClosedDays(_root));
    }

    [Fact]
    public void A_write_keeps_what_it_does_not_know_and_the_line_endings()
    {
        System.IO.File.WriteAllText(File, "{\r\n  \"theirs\": { \"keep\": true },\r\n  \"inbox\": { \"other\": 1 }\r\n}\r\n", new UTF8Encoding(true));

        Assert.Null(ParleySettings.WriteClosedDays(_root, 14));

        var written = System.IO.File.ReadAllText(File);

        Assert.Contains("\"keep\": true", written, StringComparison.Ordinal);
        Assert.Contains("\"other\": 1", written, StringComparison.Ordinal);
        Assert.Contains("\r\n", written, StringComparison.Ordinal);
        Assert.Equal(14, ParleySettings.ReadClosedDays(_root));
        Assert.Equal([0xEF, 0xBB, 0xBF], System.IO.File.ReadAllBytes(File)[..3]);
    }

    [Fact]
    public void A_file_that_is_not_json_is_never_written_over()
    {
        System.IO.File.WriteAllText(File, "somebody's notes");

        Assert.NotNull(ParleySettings.WriteClosedDays(_root, 14));
        Assert.Equal("somebody's notes", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void A_file_whose_inbox_is_not_an_object_is_refused_and_never_written_over()
    {
        // Writing a window into it would replace "seven" with an object, losing what somebody wrote there.
        const string Theirs = """{"inbox":"seven"}""";

        System.IO.File.WriteAllText(File, Theirs);

        var refused = ParleySettings.WriteClosedDays(_root, 14);

        Assert.Equal($"{File} has an \"inbox\" that is not an object, so it was left as it is.", refused);
        Assert.Equal(Theirs, System.IO.File.ReadAllText(File));
        Assert.Equal(7, ParleySettings.ReadClosedDays(_root));
    }

    [Fact]
    public void A_file_that_is_json_but_not_an_object_is_refused_and_never_written_over()
    {
        System.IO.File.WriteAllText(File, "[]");

        var refused = ParleySettings.WriteClosedDays(_root, 14);

        Assert.Equal($"{File} does not hold a JSON object, so it was left as it is.", refused);
        Assert.Equal("[]", System.IO.File.ReadAllText(File));
        Assert.Equal(7, ParleySettings.ReadClosedDays(_root));
    }
}
