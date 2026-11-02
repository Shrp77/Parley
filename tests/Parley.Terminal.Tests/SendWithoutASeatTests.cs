using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What Enter says when the terminal is not connected as the person and the conversation has no plan to send by.
/// </summary>
/// <remarks>
/// <b>The owner met "still being read" for minutes.</b> The room is built only through a seat, so a seat the broker would
/// not give left the conversation read and the draft never made — and the send said the conversation was still being read,
/// which stopped being true on the first press and never changed. The sentence has to be the true one.
/// </remarks>
[Integration]
public sealed class SendWithoutASeatTests
{
    [Fact]
    public async Task Enter_with_no_seat_says_it_is_not_connected_rather_than_that_the_conversation_is_being_read()
    {
        // Must fail against the early return that said "still being read" whatever the seat was.
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        Assert.Null(shell.Held);
        Assert.Null(shell.Drafting);

        shell.Line.Written = "Can you look at the build?";
        shell.Line.Accept();
        await shell.Settled;

        Assert.Contains("not connected", shell.Said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("still being read", shell.Said, StringComparison.Ordinal);
    }
}
