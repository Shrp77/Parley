using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a placed file is created, read and removed and never changed, and that the person places files through the
/// broker rather than by copying them into a folder (SPEC.md §9.5, §10.2 <c>place_persons_file</c>).
/// </summary>
/// <remarks>
/// The broker no longer watches the folders. A watcher guessed from disk events whether a file had been edited,
/// renamed or replaced, and every guess needed a timing window in which an agent could end up holding the person's
/// file. Without one, a file enters only by placement, and bytes changed by hand are refused rather than taken in.
/// </remarks>
public sealed class APlacedFileIsNeverChangedTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task The_person_places_a_file_that_is_theirs_and_no_agent_can_remove()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        var placed = await FileWire.PlaceAsPersonAsync(operators, "brief.md", Text("the person's brief"), "pricing", Ct);

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal(string.Empty, Assert.Single(
            (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files,
            f => f.Name == "brief.md").PlacedBy);

        var removed = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "brief.md" } } },
            cancellationToken: Ct);

        Assert.Empty(removed.Removed);
        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, Assert.Single(removed.Refused).Rejection);
    }

    [Fact]
    public async Task The_persons_placement_is_refused_into_a_conversation_that_does_not_exist_and_over_other_content()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        var nowhere = await FileWire.PlaceAsPersonAsync(operators, "brief.md", Text("x"), "no-such-talk", Ct);
        var overAgents = await FileWire.PlaceAsPersonAsync(operators, "terms.md", Text("net 60"), "pricing", Ct);
        var first = await FileWire.PlaceAsPersonAsync(operators, "brief.md", Text("one"), "pricing", Ct);
        var second = await FileWire.PlaceAsPersonAsync(operators, "brief.md", Text("two"), "pricing", Ct);
        var again = await FileWire.PlaceAsPersonAsync(operators, "brief.md", Text("one"), "pricing", Ct);

        Assert.Equal(Wire.FileRejection.ConversationNotAssignable, nowhere.Rejection);
        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, overAgents.Rejection);
        Assert.Equal(Wire.PlaceOutcome.Placed, first.Outcome);
        Assert.Equal(Wire.FileRejection.NameHoldsOtherContent, second.Rejection);
        Assert.Equal(Wire.PlaceOutcome.AlreadyCurrent, again.Outcome);
        Assert.Equal("net 30", await File.ReadAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md"), Ct));
    }

    [Fact]
    public async Task An_agent_cannot_remove_its_file_once_its_bytes_were_changed_by_hand()
    {
        // The bytes on disk are not what the agent placed, so they may be the person's; the agent never deletes them.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("the agent's"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        await File.WriteAllTextAsync(path, "the person's own, longer", Ct);

        var removed = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "notes.md" } } },
            cancellationToken: Ct);

        Assert.Empty(removed.Removed);
        Assert.Equal(Wire.FileRejection.Changed, Assert.Single(removed.Refused).Rejection);
        Assert.Equal("the person's own, longer", await File.ReadAllTextAsync(path, Ct));

        // The person removes it.
        var byPerson = await broker.ConnectAsOperator().RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { ConversationId = "pricing", Name = "notes.md" } } },
            cancellationToken: Ct);

        Assert.Single(byPerson.Removed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_fetch_of_bytes_changed_by_hand_is_refused_saying_a_new_version_takes_a_new_name()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        await File.WriteAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md"), "gross", Ct);

        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "notes.md", ConversationId = "pricing" }, cancellationToken: Ct);

        Wire.FileRefusal? refusal = null;

        while (await call.ResponseStream.MoveNext(Ct))
        {
            refusal ??= call.ResponseStream.Current.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal
                ? call.ResponseStream.Current.Refusal
                : null;
        }

        Assert.NotNull(refusal);
        Assert.Equal(Wire.FileRejection.Changed, refusal!.Rejection);
        Assert.Contains("new name", refusal.Detail, StringComparison.Ordinal);

        // The record keeps what was placed.
        var row = Assert.Single((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
        Assert.Equal(placed.Sha256, row.Sha256);
    }
}
