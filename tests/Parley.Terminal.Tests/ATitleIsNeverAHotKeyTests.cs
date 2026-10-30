using System.Text.RegularExpressions;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That no frame's or dialog's title is read for a hot key: titles carry somebody else's text, drawn as it is spelled.
/// </summary>
/// <remarks>
/// <b>Found by review, round 52.</b> A frame takes an underscore in its title as marking a hot key, and the transcript's
/// title carries a conversation id (an agent's choice, which allows <c>_</c>) and a roadmap item's title. So the
/// underscore vanished from the frame, the letter after it became the transcript's key and swallowed the Alt action of
/// the same letter, and a title changed off the loop while a draw formatted the old one threw in the toolkit, ending
/// the terminal, in about one draw in twenty-five.
/// </remarks>
[Integration]
public sealed class ATitleIsNeverAHotKeyTests
{
    private static ConversationExport WithItem(string title) =>
        new([], [], null, new RoadmapItemRef(1, 1, title, 1, false));

    [Fact]
    public void Every_frame_and_dialog_turns_hot_keys_off()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var missing = new List<string>();

        foreach (var file in new DirectoryInfo(Path.Combine(directory!.FullName, "src", "Parley.Terminal")).EnumerateFiles("*.cs"))
        {
            var text = File.ReadAllText(file.FullName);
            var classes = Regex.Matches(text, @"class \w+(\([^)]*\))?\s*:\s*(FrameView|Dialog|Window)\b").Count;
            var turnedOff = Regex.Matches(text, @"HotKeySpecifier = (Parley\.Terminal\.)?Plain\.NoHotKey;").Count;

            if (classes > turnedOff)
            {
                missing.Add($"{file.Name}: {classes} frames or dialogs, {turnedOff} with hot keys off");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("fix_build", null)]
    [InlineData("rm1-1", "Make_flaky tests pass")]
    public void A_title_with_an_underscore_gives_the_transcript_no_hot_key(string conversation, string? itemTitle)
    {
        var region = new TranscriptRegion();

        region.Show(conversation, "alpha", itemTitle is null ? new ConversationExport([], []) : WithItem(itemTitle), null);

        Assert.Equal(Key.Empty, region.HotKey);
    }

    [Fact]
    public void The_frame_names_the_conversation_as_it_is_spelled()
    {
        using var driving = Driving.Open(width: 100, height: 10);
        var window = new Window { BorderStyle = LineStyle.None };
        var region = new TranscriptRegion { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        window.Add(region);
        driving.Show(window);

        region.Show("fix_build", "alpha", new ConversationExport([], []), null);
        driving.App.LayoutAndDraw(true);

        Assert.Contains("fix_build", driving.Text(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x_f", null)]
    [InlineData(null, "Make_flaky tests pass")]
    public async Task An_underscore_in_the_title_does_not_swallow_an_action_key(string? conversation, string? itemTitle)
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        shell.Transcript.Show(
            conversation ?? Seated.Conversation,
            Walking.Alpha.Value,
            itemTitle is null ? new ConversationExport([], []) : WithItem(itemTitle),
            null);

        seated.Focus<TranscriptRegion>();

        shell.NewKeyDownEvent(new Key(Shell.FoldKey).WithAlt);

        await shell.Settled;

        Assert.True(shell.Transcript.Folded, $"hotkey {shell.Transcript.HotKey}; title {shell.Transcript.Title}");
    }

    [Fact]
    public async Task A_transcript_retitled_while_it_draws_does_not_throw()
    {
        using var driving = Driving.Open(width: 100, height: 20);
        var window = new Window { BorderStyle = LineStyle.None };
        var region = new TranscriptRegion { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        window.Add(region);
        driving.Show(window);

        var failures = new List<Exception>();
        var stop = false;
        var withItem = WithItem("Make every_thing pass");

        // Retitled from another thread, as a read landing on the pool does, between a title with an underscore and one
        // without. With hot keys on, about one draw in twenty-five threw, so five seconds fail it many times over.
        var writer = Task.Factory.StartNew(
            () =>
            {
                var round = 0;

                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        if (round++ % 2 == 0)
                        {
                            region.Show("rm1-1", "alpha", withItem, null);
                        }
                        else
                        {
                            region.Show("rm1-1", "alpha", null, "down");
                        }
                    }
                    catch (Exception failure)
                    {
                        lock (failures)
                        {
                            failures.Add(failure);
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var draws = 0;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                driving.App.LayoutAndDraw(true);
                draws++;
            }
            catch (Exception failure)
            {
                lock (failures)
                {
                    failures.Add(failure);
                }
            }
        }

        Volatile.Write(ref stop, true);
        await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        lock (failures)
        {
            Assert.True(failures.Count == 0, $"{failures.Count} failures in {draws} draws, first: {failures.FirstOrDefault()}");
        }
    }
}
