using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// The conversations half of a project view: what it offers, and the door in front of a destroy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The door is the point.</b> 004 admitted destroying a project and 005 purging an agent, both
/// on the owner's terms — a surface that can create a thing must be able to remove it — and both
/// behind a description and the thing's own name typed back. A conversation is the third subject
/// and takes the same door, so these probe it the way <c>WithdrawingTests</c> probes the second:
/// by typing the wrong thing and watching nothing happen.
/// </para>
/// <para>
/// Archiving deliberately has no door. Nothing is destroyed and one keystroke reverses it, so a
/// confirmation there would be a dialog in front of a light switch — and the line 004 drew is
/// about description and deliberate act, not about ceremony.
/// </para>
/// </remarks>
public sealed class ConversationActionsTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static ConversationSummary Conversation(
        string id, bool archived = false, bool open = false) =>
        new(
            id,
            [AgentName.Create("orders@test"), AgentName.Create("billing@test")],
            4,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            open,
            archived);

    private static async Task<Shell> OnConversationsAsync(
        Driving.Session driving, BrokerDirectory directory, params ConversationSummary[] shown)
    {
        var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Conversations.Show(shown, null);
        shell.Focus(1);

        return shell;
    }

    [Fact]
    public async Task The_conversations_pane_offers_sorting_and_the_archived_toggle()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk"));

        Assert.Contains(shell.Actions(), a => a.Label == Shell.SortTitle);
        Assert.Contains(shell.Actions(), a => a.Label == Shell.ShowArchivedTitle);
    }

    [Fact]
    public async Task Archiving_is_offered_for_the_highlighted_conversation_and_names_the_direction()
    {
        using var driving = Driving.Open();
        using var directory = Directory();

        using (var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk")))
        {
            Assert.Contains(shell.Actions(), a => a.Label == Shell.ArchiveTitle);
        }

        using var archived = await OnConversationsAsync(
            driving, directory, Conversation("alpha-talk", archived: true));

        // The label says which way it goes, so somebody reading the action list is not told to
        // archive a thing that is already archived.
        Assert.Contains(archived.Actions(), a => a.Label == Shell.UnarchiveTitle);
    }

    [Fact]
    public async Task The_archived_toggle_says_which_way_it_goes_too()
    {
        // Same property as the archive label, on the list rather than on a row: an action offering
        // to "show archived conversations" while they are already shown is an action that lies
        // about what pressing it does.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk"));

        Assert.Contains(shell.Actions(), a => a.Label == Shell.ShowArchivedTitle);

        var toggle = shell.Actions().Single(a => a.Label == Shell.ShowArchivedTitle);

        await toggle.Run();
        await shell.Settled;

        Assert.Contains(shell.Actions(), a => a.Label == Shell.HideArchivedTitle);
        Assert.DoesNotContain(shell.Actions(), a => a.Label == Shell.ShowArchivedTitle);
    }

    [Fact]
    public async Task Nothing_is_offered_for_a_conversation_when_there_are_none()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory);

        Assert.DoesNotContain(shell.Actions(), a => a.Label == Shell.ArchiveTitle);
        Assert.DoesNotContain(shell.Actions(), a => a.Label == Destroying.Title);

        // Sorting and the toggle stay, because both are about the list rather than about a row --
        // and turning the toggle on is how somebody with an empty list finds out it is not empty.
        Assert.Contains(shell.Actions(), a => a.Label == Shell.ShowArchivedTitle);
    }

    [Fact]
    public async Task Destroying_describes_what_will_be_lost_before_asking_anything()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk"));

        await shell.DestroyConversation();

        var dialog = driving.Find(Destroying.Title);
        var text = driving.Text();

        Assert.NotNull(dialog);
        Assert.Contains("alpha-talk", text, StringComparison.Ordinal);
        Assert.Contains("orders@test", text, StringComparison.Ordinal);
        Assert.Contains("4 messages", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeping_is_offered_first_and_is_what_enter_does()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk"));

        await shell.DestroyConversation();

        var choosing = (ChoiceDialog)driving.Find(Destroying.Title);

        // Keep is first, so Enter without moving is the harmless answer.
        driving.Row(choosing.Choices, Destroying.Keep);
        driving.Enter();
        await shell.Settled;

        Assert.Equal(Destroying.Kept, shell.Said);
    }

    [Fact]
    public async Task The_wrong_identifier_destroys_nothing()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(driving, directory, Conversation("alpha-talk"));

        await shell.DestroyConversation();

        var choosing = (ChoiceDialog)driving.Find(Destroying.Title);

        driving.Row(choosing.Choices, Destroying.Destroy);
        driving.Enter();

        var typing = (TextDialog)driving.Find(Destroying.Title);

        driving.Type("alpha-tal");
        typing.Finish(taken: true);

        await shell.Settled;

        Assert.Equal(Destroying.WrongName, shell.Said);
    }

    [Fact]
    public async Task An_open_exchange_is_named_before_the_identifier_is_asked_for()
    {
        // The consequence an operator is least likely to predict, and the reason it comes first:
        // the exchange does not orphan and is not ghosted, so whoever is waiting is never told
        // anything at all -- not even the ghost §7 guarantees for a question nobody answers.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(
            driving, directory, Conversation("alpha-talk", open: true));

        await shell.DestroyConversation();

        var choosing = (ChoiceDialog)driving.Find(Destroying.Title);

        driving.Row(choosing.Choices, Destroying.Destroy);
        driving.Enter();

        // Not the typed name yet.
        var consequence = (ChoiceDialog)driving.Find(Destroying.ConsequenceTitle);

        Assert.Equal(Destroying.Keep, consequence.Choices.Source!.ToList()[0]?.ToString());
        Assert.Contains("never told anything at all", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_consequence_stops_before_the_identifier()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = await OnConversationsAsync(
            driving, directory, Conversation("alpha-talk", open: true));

        await shell.DestroyConversation();

        var first = (ChoiceDialog)driving.Find(Destroying.Title);

        driving.Row(first.Choices, Destroying.Destroy);
        driving.Enter();

        var consequence = (ChoiceDialog)driving.Find(Destroying.ConsequenceTitle);

        driving.Row(consequence.Choices, Destroying.Keep);
        driving.Enter();

        await shell.Settled;

        Assert.Equal(Destroying.Kept, shell.Said);
        Assert.False(driving.Has(Destroying.TypeItBack));
    }
}
