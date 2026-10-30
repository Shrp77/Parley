using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// A Request and its Result read in the terminal's transcript.
/// </summary>
/// <remarks>
/// Read from the drawn screen rather than from <c>Reading.Lines</c>: a line the derivation produced and the region
/// never drew is exactly what the client tests cannot see.
/// </remarks>
[Integration]
public sealed class AskOnScreenTests
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

    [Fact]
    public async Task A_request_is_drawn_as_its_ask_and_not_as_the_document_it_came_in()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        await shell.Start();
        await Walking.GoAsync(shell, Viewing.Conversation, "pricing");

        shell.Transcript.Show(
            "pricing",
            "alpha",
            new ConversationExport(
                [
                    Message(
                        1,
                        MessageType.Request,
                        "orders@test",
                        "billing@test",
                        """{"ask":"Can invoices use the price the order supplies?"}"""),
                    Message(
                        2,
                        MessageType.Result,
                        "billing@test",
                        "orders@test",
                        """{"outcomes":[{"id":"request","outcome":"answered","text":"schema 12"}]}"""),
                ],
                [],
                null),
            null);

        driving.App.LayoutAndDraw(true);

        var written = shell.Transcript.Written;

        Assert.Contains("Can invoices use the price the order supplies?", written, StringComparison.Ordinal);
        Assert.Contains("schema 12", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ask\"", written, StringComparison.Ordinal);
    }
}
