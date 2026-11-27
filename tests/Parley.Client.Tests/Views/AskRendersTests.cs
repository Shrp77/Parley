using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// A Request reads as its ask and a Result as one line per outcome (SPEC.md §5.1, §5.3).
/// </summary>
/// <remarks>
/// A reader that had not learned <c>ask</c> would fall through to the raw document, which looks almost right on a
/// screen and which nobody can read, so the complement is asserted too.
/// </remarks>
[Unit]
public sealed class AskRendersTests
{
    private static Message Message(MessageType type, string payload) =>
        new(
            1,
            1,
            type,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "some-model-9",
            payload,
            "pricing",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            null);

    private static string Written(MessageType type, string payload) =>
        string.Join("\n", Reading.Lines([Message(type, payload)]).Select(l => l.Text));

    [Fact]
    public void A_request_shows_the_ask_and_nothing_a_reader_would_have_to_explain()
    {
        var lines = Reading.Lines([Message(MessageType.Request, """{"ask":"Can invoices use the price the order supplies?"}""")]);

        Assert.Contains(lines, l => l.Text.Contains("Can invoices use the price", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Kind == LineKind.Point);
        Assert.DoesNotContain(lines, l => l.Text.Contains("\"ask\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_result_shows_one_line_per_outcome_with_its_outcome_and_sources()
    {
        var said = Written(
            MessageType.Result,
            """{"outcomes":[{"id":"request","outcome":"answered","sources":["inventory@test"],"text":"It can: the order carries it."}]}""");

        Assert.Contains("It can: the order carries it.", said, StringComparison.Ordinal);
        Assert.Contains("answered", said, StringComparison.Ordinal);
        Assert.Contains("inventory@test", said, StringComparison.Ordinal);
    }
}
