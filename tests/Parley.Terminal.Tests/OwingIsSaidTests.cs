using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what a person sends says what it costs the peer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported as confusion rather than as a defect, which is what made it one.</b> The owner read
/// "Ask somebody a question" and "Tell somebody something", and proposed that Tell should be
/// "fire and forget" and Ask "start a conversation". Both proposals named a property the two shared,
/// and 0.5 took the owner's underlying point further: every message is answered, so there is one
/// thing to send and it opens an exchange the peer owes a Result on.
/// </para>
/// <para>
/// So the debt is what the words say. They are <see cref="Owing"/>'s, shared with the console so the
/// two surfaces cannot describe one protocol two ways.
/// </para>
/// </remarks>
public sealed class OwingIsSaidTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public void The_words_state_the_debt_and_not_something_every_send_shares()
    {
        // Pinned because both wrong answers are tempting and one of them was proposed. "Fire and forget"
        // is true of every send, since nobody waits (§4.2); what a Request adds is a Result owed.
        Assert.Contains("owe you an answer", Owing.Requesting, StringComparison.Ordinal);
        Assert.DoesNotContain("fire and forget", Owing.Requesting, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_status_line_stays_short()
    {
        // A menu is a list somebody reads, a status line is a reminder. A clause on the status line would
        // push every action behind `?` at any ordinary width.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Focus(1);

        Assert.DoesNotContain(Owing.Requesting, shell.Status, StringComparison.Ordinal);

        // The labels themselves carry no clause, which is what keeps the line short.
        foreach (var action in shell.Actions())
        {
            Assert.DoesNotContain("—", action.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Starting_and_continuing_are_told_apart_by_name()
    {
        // The same composer either way; what differs is whether it makes a conversation. From the list it
        // does, and says so; from a transcript it adds to the one on screen, and does not claim to start one.
        Assert.StartsWith("Start a conversation", Participation.StartTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("Start a conversation", Participation.RequestTitle, StringComparison.Ordinal);
    }
}
