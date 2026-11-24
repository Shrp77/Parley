using System.Text;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The two writers serve and up wrap the console in keep each promise their remarks make, for every kind of write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SurvivingWriter"/> takes only an <see cref="IOException"/> as the terminal going: anything else is a fault
/// and still throws, and the writer goes on trying. <see cref="TeeWriter"/> writes the log even when the console throws,
/// for a single character, a string, a line and a flush alike, not only for a line.
/// </para>
/// <para>
/// And the entry point hands serve a surviving error writer as well as a surviving output, and its last sweep one too:
/// the web UI's warnings are copied to the error writer, so a closed terminal there would otherwise throw on the
/// process reader's thread or out of a broker's report. Read from the source, as
/// <see cref="AClosedTerminalEndsNoNodeTests"/> reads it.
/// </para>
/// </remarks>
[Unit]
public sealed class TheConsoleWritersHoldTheirWordTests
{
    public static TheoryData<string> Writes() => new() { "char", "string", "line", "flush" };

    public static TheoryData<string> EveryWrite() =>
        new() { "char", "string", "chars", "line", "empty line", "flush", "async line", "formatted" };

    [Theory]
    [MemberData(nameof(EveryWrite))]
    public async Task Every_kind_of_write_to_a_terminal_that_has_gone_is_dropped(string write)
    {
        var console = new SurvivingWriter(new Faulty(() => new IOException("Input/output error")));

        switch (write)
        {
            case "chars":
                console.Write("started.".ToCharArray(), 1, 3);
                break;
            case "empty line":
                console.WriteLine();
                break;
            case "async line":
                await console.WriteLineAsync("  orders@alpha: started.");
                break;
            case "formatted":
                console.Write("  {0}: started.", "orders@alpha");
                break;
            default:
                Write(console, write);
                break;
        }

        Assert.True(console.Gone, $"a {write} write that failed did not mark the terminal gone.");
    }

    [Fact]
    public void A_fault_that_is_not_the_terminal_going_still_throws_and_the_next_write_is_tried()
    {
        var faulty = new Faulty(() => new InvalidOperationException("a fault, not a closed terminal"));
        var console = new SurvivingWriter(faulty);

        _ = Assert.Throws<InvalidOperationException>(() => console.WriteLine("  orders@alpha: started."));
        Assert.False(console.Gone);

        _ = Assert.Throws<InvalidOperationException>(() => console.WriteLine("  billing@alpha: started."));
        Assert.Equal(2, faulty.Tries);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public void The_log_is_written_whichever_way_the_console_is_written_when_it_throws(string write)
    {
        var log = new Recording();
        var tee = new TeeWriter(new Faulty(() => new IOException("Input/output error")), log);

        _ = Assert.Throws<IOException>(() => Write(tee, write));

        Assert.Equal(1, log.Calls);
    }

    [Theory]
    [InlineData("return ServeCommand.RunAsync(", "new SurvivingWriter(Console.Error)", " Console.Error,")]
    [InlineData("await NodeCommand.StopEveryoneAsync(residents,", "new SurvivingWriter(Console.Out)", " Console.Out)")]
    public void Serve_hands_on_only_writers_that_survive_the_terminal(string call, string survives, string raw)
    {
        var source = string.Join(' ', RepositoryFile.Read("src/Parley.Cli/Program.cs")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var at = source.IndexOf(call, StringComparison.Ordinal);
        Assert.True(at >= 0, $"Program.cs no longer has '{call}'; move this guard with it.");

        var arguments = source[at..source.IndexOf(';', at)];

        Assert.Contains(survives, arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, arguments, StringComparison.Ordinal);
    }

    private static void Write(TextWriter to, string write)
    {
        switch (write)
        {
            case "char":
                to.Write('x');
                break;
            case "string":
                to.Write("orders@alpha: started.");
                break;
            case "line":
                to.WriteLine("orders@alpha: started.");
                break;
            default:
                to.Flush();
                break;
        }
    }

    private sealed class Faulty(Func<Exception> fault) : TextWriter
    {
        public int Tries { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Fail();

        public override void Write(string? value) => Fail();

        public override void WriteLine(string? value) => Fail();

        public override void Flush() => Fail();

        private void Fail()
        {
            Tries++;

            throw fault();
        }
    }

    private sealed class Recording : TextWriter
    {
        public int Calls { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Calls++;

        public override void Write(string? value) => Calls++;

        public override void WriteLine(string? value) => Calls++;

        public override void Flush() => Calls++;
    }
}
