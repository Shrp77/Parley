using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the web UI writes is passed on to the broker's own output, and its error output, with its control characters
/// neutralised.
/// </summary>
/// <remarks>
/// The web UI's log carries text it did not write: a request path is logged as it was asked for, decoded, by a request
/// the console then refuses (a cross-site page's image, a rebinding name). Copied raw, an escape sequence in one clears
/// or retitles the person's terminal.
/// </remarks>
[Unit]
public sealed class TheWebUisOutputIsDrawnInertTests
{
    public static TheoryData<int[]> Hostile() => new()
    {
        new[] { 0x1b, '[', '2', 'J' },
        new[] { 0x1b, ']', '0', ';', 't', 0x07 },
        new[] { 'a', 0x85, 'b', 0x9b, 'c', 0x2028, 'd', 0x202e, 'e' },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_line_the_web_ui_writes_reaches_the_broker_with_no_control_character(int[] codes)
    {
        var line = Line(codes);
        using var log = new StringWriter();
        var said = new WebUiReadiness.Said(log);

        said.Line(line);

        AssertInert(line, log.ToString());
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_line_the_web_ui_writes_on_its_errors_reaches_the_broker_with_no_control_character(int[] codes)
    {
        var line = Line(codes);
        using var errors = new StringWriter();

        WebUiReadiness.Pass(errors, line);

        AssertInert(line, errors.ToString());
    }

    private static string Line(int[] codes) =>
        "      Request starting HTTP/1.1 GET http://127.0.0.1:5280/x"
        + new string([.. codes.Select(code => (char)code)]) + " - - -";

    private static void AssertInert(string line, string passed)
    {
        var written = passed.TrimEnd('\r', '\n');

        // One for one, so nothing a line says is lost and nothing is moved.
        Assert.Equal(line.Length, written.Length);
        Assert.DoesNotContain(
            written,
            character => char.IsControl(character) || character is (char)0x2028 or (char)0x2029 or (char)0x202e);
    }
}
