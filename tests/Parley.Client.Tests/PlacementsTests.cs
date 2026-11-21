using Parley.Client.Files;
using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a placed file sits in a transcript by its placement time and that Media lists newest first (SPEC.md §9.5).
/// </summary>
[Unit]
public sealed class PlacementsTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static SharedFile File(string name, DateTimeOffset at, string? by = "billing@test") => new(
        "pricing", "pricing", SharedFileName.Create(name), 2048, ContentHash.Of(System.Text.Encoding.UTF8.GetBytes(name)),
        by is null ? null : AgentName.Create(by), at);

    private static Message Said(long id, DateTimeOffset at) => new(
        id, id, MessageType.Request, AgentName.Create("orders@test"), AgentName.Create("billing@test"), "test",
        """{"ask":"What does it look like?"}""", "pricing", at, null);

    [Fact]
    public void Media_puts_images_apart_from_other_files_each_newest_first()
    {
        var (images, others) = Placements.Media(
        [
            File("old.png", Ten),
            File("notes.md", Ten.AddMinutes(1)),
            File("new.webp", Ten.AddMinutes(2)),
            File("later.txt", Ten.AddMinutes(3)),
        ]);

        Assert.Equal(["new.webp", "old.png"], images.Select(f => f.Name.Value));
        Assert.Equal(["later.txt", "notes.md"], others.Select(f => f.Name.Value));
    }

    [Fact]
    public void A_queue_gives_up_only_what_was_placed_before_the_moment()
    {
        var waiting = new Queue<SharedFile>(Placements.InOrder([File("b.png", Ten.AddMinutes(5)), File("a.png", Ten)]));

        Assert.Equal(["a.png"], Placements.Before(waiting, Ten.AddMinutes(1)).Select(f => f.Name.Value));
        Assert.Empty(Placements.Before(waiting, Ten.AddMinutes(5)));
        Assert.Single(waiting);
    }

    [Fact]
    public void The_terminal_transcript_draws_a_placement_between_the_messages_around_it_and_one_after_them_at_the_end()
    {
        var export = new ConversationExport([Said(1, Ten), Said(2, Ten.AddMinutes(10))], []);

        var lines = Reading.Lines(
                export,
                TimeZoneInfo.Utc,
                "pricing",
                [File("mockup.png", Ten.AddMinutes(5)), File("notes.md", Ten.AddMinutes(20), by: null)])
            .Select(line => line.Text)
            .ToList();

        var mockup = lines.FindIndex(l => l.Contains("billing@test placed mockup.png (image, 2 KiB)", StringComparison.Ordinal));
        var second = lines.FindIndex(l => l.Contains("10:10", StringComparison.Ordinal) && !l.StartsWith(' '));
        var notes = lines.FindIndex(l => l.Contains("the person placed notes.md (text, 2 KiB)", StringComparison.Ordinal));

        Assert.True(mockup > 0 && second > mockup && notes > second, string.Join("\n", lines));
        Assert.StartsWith(" ", lines[mockup], StringComparison.Ordinal);
    }

    [Fact]
    public void A_transcript_without_files_is_what_it_was()
    {
        var export = new ConversationExport([Said(1, Ten)], []);

        Assert.Equal(
            Reading.Lines(export, TimeZoneInfo.Utc, "pricing").Select(l => l.Text),
            Reading.Lines(export, TimeZoneInfo.Utc, "pricing", []).Select(l => l.Text));
    }
}
