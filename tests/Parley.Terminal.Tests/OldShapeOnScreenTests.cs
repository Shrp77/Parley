using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// A conversation written under 0.5 or 0.6 still reads in the terminal's transcript.
/// </summary>
/// <remarks>
/// <para>
/// FR-009 on the third surface. The derivation is checked in <c>Parley.Client.Tests</c> and the page in
/// <c>Parley.Web.Tests</c>; what is left here is the region that draws the lines — where the failure would be
/// silent, because a transcript missing a line still draws, still scrolls, and still says nothing is wrong.
/// </para>
/// <para>
/// Read from the drawn screen rather than from <c>Reading.Lines</c>, which is the point of doing it again here:
/// a line the derivation produced and the region never drew is exactly what the other two tests cannot see.
/// </para>
/// </remarks>
public sealed class OldShapeOnScreenTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static Message Message(long id, MessageType type, string from, string to, string payload) =>
        new(
            id,
            7,
            type,
            AgentName.Create(from),
            AgentName.Create(to),
            "claude",
            payload,
            "pricing",
            DateTimeOffset.UnixEpoch,
            null);

    private static ConversationExport Of(params Message[] messages) => new([.. messages], [], null);

    private static async Task<string> DrawnAsync(Driving.Session driving, Shell shell, ConversationExport conversation)
    {
        await Walking.GoAsync(shell, Viewing.Conversation, "pricing");

        shell.Transcript.Show("pricing", "alpha", conversation, null);

        driving.App.LayoutAndDraw(true);

        return shell.Transcript.Written;
    }

    [Fact]
    public async Task A_0_6_request_and_result_are_drawn_with_every_field_they_carry()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        await shell.Start();

        var written = await DrawnAsync(driving, shell, Of(
            Message(
                1,
                MessageType.Request,
                "orders@test",
                "billing@test",
                """{"intent":"deciding where tax is added","items":[{"id":"r1","kind":"work","text":"use the order's price"},{"id":"r2","kind":"question","text":"which invoice version?"}],"done_when":"invoices show the order's price"}"""),
            Message(
                2,
                MessageType.Result,
                "billing@test",
                "orders@test",
                """{"outcomes":[{"id":"r1","outcome":"done","text":"merged in 4.2"},{"id":"r2","outcome":"answered","text":"schema 12"}]}""")));

        foreach (var shown in new[]
        {
            "deciding where tax is added",
            "use the order's price",
            "which invoice version?",
            "invoices show the order's price",
            "merged in 4.2",
            "schema 12",
        })
        {
            Assert.Contains(shown, written, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_0_7_request_is_drawn_as_its_ask_and_not_as_the_document_it_came_in()
    {
        // The complement, and the likelier regression: a reader that had not learned `ask` would fall through to
        // showing the raw JSON, which looks like a transcript at a glance and is one nobody can read.
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        await shell.Start();

        var written = await DrawnAsync(driving, shell, Of(
            Message(
                1,
                MessageType.Request,
                "orders@test",
                "billing@test",
                """{"ask":"Can invoices use the price the order supplies?"}""")));

        Assert.Contains("Can invoices use the price the order supplies?", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ask\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Done when", written, StringComparison.Ordinal);
    }
}
