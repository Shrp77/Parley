using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That two brokers naming one log both get to write it.
/// </summary>
/// <remarks>
/// A scheduled task and a terminal restoring its session both start the broker with the same
/// <c>--log</c>. The log must not be held open for writing, or the second could not open it, and the
/// one line that would explain stranded agents would go to a console nobody could see.
/// </remarks>
[Integration]
public sealed class SharedLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-log-" + Guid.NewGuid().ToString("N"));

    public SharedLogTests() => Directory.CreateDirectory(_directory);

    private string Log => Path.Combine(_directory, "parley-broker.log");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this suite.
        }
    }

    [Fact]
    public void Two_brokers_writing_one_log_lose_neither_line()
    {
        // The first writer keeps going after the second has written. A writer holding its own
        // position would put its next line over the second's, which is the quieter way to lose it.
        using var first = Logging.Open(Log, TextWriter.Null);
        using var second = Logging.Open(Log, TextWriter.Null);

        Assert.NotNull(first);
        Assert.NotNull(second);

        first.WriteLine("first: serving");
        second.WriteLine("second: already served, exiting");
        first.WriteLine("first: still serving");

        Assert.Equal(["first: serving", "second: already served, exiting", "first: still serving"], File.ReadAllLines(Log));
    }

    [Fact]
    public void A_line_reaches_the_file_only_once_it_is_whole()
    {
        // So that two processes interleave lines rather than characters.
        using var writer = Logging.Open(Log, TextWriter.Null);

        Assert.NotNull(writer);

        writer.Write("half");

        Assert.Equal(string.Empty, File.ReadAllText(Log));

        writer.WriteLine(" and the rest");

        Assert.Equal(["half and the rest"], File.ReadAllLines(Log));
    }

    [Fact]
    public void An_unfinished_last_line_is_not_lost_when_the_log_closes()
    {
        // The last thing a stopping process says is the line that mattered.
        var writer = Logging.Open(Log, TextWriter.Null);

        Assert.NotNull(writer);

        writer.Write("last words");
        writer.Dispose();

        Assert.Equal("last words", File.ReadAllText(Log));
    }

    [Fact]
    public void An_earlier_run_is_appended_to_rather_than_replaced()
    {
        File.WriteAllText(Log, "yesterday" + Environment.NewLine);

        using (var writer = Logging.Open(Log, TextWriter.Null))
        {
            writer!.WriteLine("today");
        }

        Assert.Equal(["yesterday", "today"], File.ReadAllLines(Log));
    }
}
