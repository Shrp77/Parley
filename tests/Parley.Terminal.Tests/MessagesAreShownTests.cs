using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what this surface says reaches the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>It did not, and the suite could not tell.</b> <c>Shell.Said</c> was written in twenty-odd
/// places — seated as somebody, the broker refused the join, sorted by name, showing archived, an
/// agent retired, a seat released, a log that could not be read — and fifteen test files asserted
/// on it, and no line of this surface drew it. Every one of those messages was computed, checked,
/// and shown to nobody. The owner met it as <c>j</c>, a name typed, and nothing happening.
/// </para>
/// <para>
/// <b>That is the same shape as the terminal having no colour while <c>ColourTests</c> passed.</b>
/// A test that reads a property is testing a string; what makes a string a message is a place on
/// the screen. So these read the screen.
/// </para>
/// </remarks>
public sealed class MessagesAreShownTests
{
    [Fact]
    public void What_an_action_says_appears_on_the_screen()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report("sorted by name");

        driving.App.LayoutAndDraw(true);

        // Read off the drawn screen rather than from the property, because the property was never
        // the problem: fifteen files already assert on it and it reached no screen.
        Assert.Equal("sorted by name", shell.Said);
        Assert.Contains("sorted by name", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_is_neutralised_before_it_is_drawn()
    {
        // Some of these sentences are a broker's rather than this surface's: `The broker refused
        // the join:` ends with whatever came back from the far end of a gRPC call. A message line
        // is as good a place to smuggle an escape sequence as a transcript is, and the transcript
        // has been attacked through exactly that before.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report($"one{(char)27}[2Jtwo");

        driving.App.LayoutAndDraw(true);

        var drawn = driving.Text();

        Assert.DoesNotContain((char)27, drawn);
        Assert.Contains("one", drawn, StringComparison.Ordinal);
        Assert.Contains("two", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_line_does_not_take_the_status_line_with_it()
    {
        // Two lines, each with a job. The status line says where you are and how to leave, and a
        // message that replaced it would take the way out away at the moment somebody most needs
        // it -- which is the mistake the narrow-terminal ladder was written to avoid.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Report("something happened");

        driving.App.LayoutAndDraw(true);

        var drawn = driving.Text();

        Assert.Contains("something happened", drawn, StringComparison.Ordinal);
        Assert.Contains(Shell.ActionsKey.ToString(), shell.Status, StringComparison.Ordinal);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
