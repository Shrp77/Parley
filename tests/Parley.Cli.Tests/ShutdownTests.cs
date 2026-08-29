using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the commands which run until stopped can actually be stopped.
/// </summary>
/// <remarks>
/// <para>
/// Written because <c>parley serve</c> ignored Ctrl+C. It was reported as "Ctrl+C, multiple
/// times, does not stop the broker", and the multiple times is the diagnostic part: repeating a
/// signal that is being swallowed cannot help.
/// </para>
/// <para>
/// The mechanism is worth keeping written down, because nothing about it is visible at the call
/// site. <c>serve</c> was invoked with no cancellation token, so its sweep loop waited on
/// <see cref="System.Threading.CancellationToken.None"/> forever. That alone would have left the
/// key to the runtime's default handler, which kills the process — except that starting a
/// <c>WebApplication</c> installs the generic host's console lifetime, which registers its own
/// handler and sets <c>Cancel = true</c>. So the signal was captured and neutralised by one
/// component, and the token it wanted somebody to observe was never passed to the other. The
/// banner the broker prints on startup said "Ctrl+C stops it" throughout.
/// </para>
/// <para>
/// <c>up</c> was wired correctly the whole time, which is what made this worth a guard rather
/// than a fix: the two commands that run until stopped had drifted apart, and only one of them
/// was tested by anybody pressing the key. Checked against the source because the entry point is
/// a file of top-level statements and is not reachable as API — the same compromise
/// <see cref="HelpTextTests"/> makes, and weaker than driving the binary, which no test can do
/// portably: sending a genuine CTRL_C_EVENT needs a console group, and a test host does not have
/// one to spare.
/// </para>
/// </remarks>
public sealed class ShutdownTests
{
    private static string Program => RepositoryFile.Read("src/Parley.Cli/Program.cs");

    [Theory]
    [InlineData("ServeCommand.RunAsync")]
    [InlineData("NodeCommand.RunAsync")]
    public void A_command_that_runs_until_stopped_is_wired_to_the_interrupt(string entryPoint)
    {
        // Both, in one theory, because the defect was the asymmetry rather than either half:
        // `up` honoured Ctrl+C throughout while `serve` ignored it, and nothing said so.
        //
        // Asserted at the call site rather than by naming the verb, because the verb's dispatch
        // block legitimately does not do the wiring — it delegates — so looking there finds a
        // block that is correct and proves nothing.
        //
        // Checked as "inside an UntilInterrupted call" rather than as a literal prefix. The first
        // version of this demanded `UntilInterrupted(token => ServeCommand.RunAsync` and failed
        // the moment serve grew a second overload to choose between — correct wiring, refused for
        // its shape. A guard that breaks on a legitimate edit teaches people to edit the guard.
        var source = string.Join(' ', Program.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var occurrences = 0;

        for (var at = source.IndexOf(entryPoint, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(entryPoint, at + 1, StringComparison.Ordinal))
        {
            occurrences++;

            var window = source[Math.Max(0, at - 400)..at];

            Assert.True(
                window.Contains("UntilInterrupted", StringComparison.Ordinal),
                $"{entryPoint} is called outside UntilInterrupted, so Ctrl+C would not reach it.");
        }

        Assert.True(occurrences > 0, $"{entryPoint} is not called at all.");
    }

    [Fact]
    public void Where_a_broker_listens_never_comes_from_where_clients_connect()
    {
        // Not a shutdown concern, but the same kind of defect and the same reason it is checked
        // against the source: the entry point is a file of top-level statements and the wiring is
        // not reachable as API.
        //
        // `autostart` installs `serve` under a supervisor, so its address is a *listen* address.
        // It was briefly taken from the same resolution clients use, which consults PARLEY_BROKER
        // — and on a machine whose agents reach a broker elsewhere, that holds somebody else's
        // host. The scheduled task then told a local broker to bind a remote address, and the
        // failure appeared at the next logon, inside a supervisor, where nobody is watching.
        var source = string.Join(' ', Program.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var at = source.IndexOf("AutostartCommand.RunAsync", StringComparison.Ordinal);
        Assert.True(at > 0, "autostart is not dispatched at all.");

        var window = source[Math.Max(0, at - 200)..Math.Min(source.Length, at + 200)];

        Assert.DoesNotContain(
            "resolvedEndpoint",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_interrupt_leaves_a_second_press_to_the_default_handler()
    {
        // The escape hatch, and the reason the handler unregisters itself rather than staying on.
        // If a graceful shutdown has itself wedged, an operator must still be able to get their
        // terminal back — and a handler that answered every press with Cancel = true is exactly
        // the state being fixed here, one level up.
        var helper = Program[Program.IndexOf("Task<int> UntilInterrupted", StringComparison.Ordinal)..];

        Assert.Contains("Console.CancelKeyPress -= Stop", helper, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Being_stopped_on_purpose_is_not_reported_as_failure()
    {
        // A broker that exits non-zero because somebody pressed Ctrl+C teaches operators to
        // ignore its exit code, which is the one signal a supervisor has.
        var helper = Program[Program.IndexOf("Task<int> UntilInterrupted", StringComparison.Ordinal)..];

        Assert.Contains("OperationCanceledException", helper, StringComparison.Ordinal);
    }
}
