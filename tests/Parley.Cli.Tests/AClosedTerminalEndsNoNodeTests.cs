using System.Text;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A terminal that closes under a running <c>parley up</c> or <c>parley serve</c> costs the node nothing, and the log
/// still gets every line.
/// </summary>
/// <remarks>
/// A disowned node whose terminal had closed got an <see cref="IOException"/> on its next console write. The node writes
/// a line when it starts an agent, so that start threw: the node lost the agent it had just started, or ended (review,
/// lens 1 run B next to 8459f8a3). The console writer now drops writes once the terminal has gone, and <c>--log</c>'s
/// tee writes the log even when the console throws.
/// </remarks>
[Unit]
public sealed class AClosedTerminalEndsNoNodeTests
{
    [Fact]
    public async Task A_write_to_a_terminal_that_has_gone_is_dropped_rather_than_thrown()
    {
        var gone = new GoneTerminal();
        var console = new SurvivingWriter(gone);

        console.WriteLine("  orders@alpha: started (a request from billing@alpha).");
        await console.WriteLineAsync("  orders@alpha: stopped.");
        console.Write('x');
        console.Flush();

        Assert.True(console.Gone);

        // Asked once: a terminal that has gone does not come back, so the rest are not tried.
        Assert.Equal(1, gone.Tries);
    }

    [Fact]
    public void A_terminal_that_is_there_is_written_to()
    {
        using var terminal = new StringWriter();
        var console = new SurvivingWriter(terminal);

        console.WriteLine("  orders@alpha: started.");

        Assert.False(console.Gone);
        Assert.Equal("  orders@alpha: started." + Environment.NewLine, terminal.ToString());
    }

    [Fact]
    public void The_log_is_written_when_the_console_throws()
    {
        using var log = new StringWriter();
        var tee = new TeeWriter(new GoneTerminal(), log);

        _ = Assert.Throws<IOException>(() => tee.WriteLine("orders@alpha: started."));

        Assert.Contains("orders@alpha: started.", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_logging_beside_a_terminal_that_has_gone_goes_on_logging()
    {
        // The arrangement serve makes: the console, made to survive, then the log.
        await using var log = new StringWriter();
        var output = TextWriter.Synchronized(new TeeWriter(new SurvivingWriter(new GoneTerminal()), log));

        await output.WriteLineAsync("  orders@alpha: started.");
        await output.WriteLineAsync("  billing@alpha: started.");

        Assert.Contains("orders@alpha: started.", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("billing@alpha: started.", log.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("async Task<int> UpAsync() =>")]
    [InlineData("return ServeCommand.RunAsync(")]
    public void Up_and_serve_hand_their_node_a_console_that_survives_the_terminal(string call)
    {
        // Read from the source, as ShutdownTests reads the same file: the entry point's own console is not something a
        // test can close under it.
        var source = string.Join(' ', RepositoryFile.Read("src/Parley.Cli/Program.cs")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var at = source.IndexOf(call, StringComparison.Ordinal);
        Assert.True(at >= 0, $"Program.cs no longer has '{call}'; move this guard with it.");

        var end = source.IndexOf(';', at);
        var arguments = source[at..end];

        Assert.Contains("new SurvivingWriter(Console.Out)", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(" Console.Out,", arguments, StringComparison.Ordinal);
    }

    private sealed class GoneTerminal : TextWriter
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

            throw new IOException("Input/output error");
        }
    }
}
