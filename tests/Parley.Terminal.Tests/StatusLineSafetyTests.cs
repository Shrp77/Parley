using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the one line always on screen cannot be made to draw something else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither name on the status line is this program's.</b> A project name comes from a manifest
/// somebody may not have written, and a conversation identifier is chosen by the agent that
/// started the conversation (SPEC.md §5.0). Both reach the line through <c>ViewPlace.Title</c>,
/// and both used to reach it unexamined: a probe put an escape sequence in an identifier and the
/// status line carried it through, which clears the operator's terminal — from a peer, by naming a
/// conversation.
/// </para>
/// <para>
/// <c>Safe</c> already existed and every region already used it. The status line was the one
/// surface that had never been asked, which is what a lens finds and reading does not: it is the
/// piece of the screen nobody thinks of as content.
/// </para>
/// </remarks>
[Integration]
public sealed class StatusLineSafetyTests
{
    /// <summary>A name that clears the screen and then writes over what was there.</summary>
    private const string Hostile = "alpha[2Jwiped";

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task A_conversation_identifier_cannot_repaint_the_terminal_from_the_status_line()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await shell.GoAsync(ViewPlace.Into(ProjectName.Create("alpha")).Into(Hostile));
        await shell.Settled;

        Assert.DoesNotContain('', shell.Status);
        Assert.DoesNotContain('', driving.Text());

        // Still named, and still the same length: a filter that dropped the characters would make
        // two identifiers that differ only by one compare equal on screen.
        Assert.Contains("wiped", shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_name_cannot_repaint_it_from_the_way_back()
    {
        // The other half. Back names where it leads, so the project's own name is on the line of
        // every conversation view -- by a different code path from the one above.
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

        using var shell = new Shell(directory);

        driving.Show(shell);

        await shell.GoAsync(new ViewPlace(Viewing.Conversation, ProjectName.Create("alpha"), Hostile));
        await shell.Settled;

        Assert.DoesNotContain('', shell.BackLabel ?? string.Empty);
    }

    [Fact]
    public async Task The_check_is_looking_at_something()
    {
        // Non-vacuity: the hostile name has to actually reach the line, or "no escape on the line"
        // is true of a line that never mentioned it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await shell.GoAsync(ViewPlace.Into(ProjectName.Create("alpha")).Into(Hostile));
        await shell.Settled;

        Assert.Contains("alpha", shell.Status, StringComparison.Ordinal);
        Assert.Contains("wiped", shell.Status, StringComparison.Ordinal);
        Assert.Contains('', Hostile);
    }
}
