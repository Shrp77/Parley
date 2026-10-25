using System.Collections.Concurrent;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>Runs alone: its tests set <see cref="Prompting.Marshal"/>, which every shell in the process reads.</summary>
[CollectionDefinition(nameof(OwnsTheMarshal), DisableParallelization = true)]
public sealed class OwnsTheMarshal;

/// <summary>
/// That the box is read and written on the thread that draws, when the program has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The harness has no loop, so nothing else holds this.</b> A driven test leaves <see cref="Prompting.Marshal"/>
/// unset and every write runs where it is made, so RefillRaceTests stays green with the box written from the pool. Here
/// the test's own thread stands in for the loop (<see cref="StandInLoop"/>), the way <c>TerminalCommand</c> hands over
/// the toolkit's <c>Invoke</c>: what is handed over from another thread waits until the loop runs it, after the keys
/// that arrived first.
/// </para>
/// <para>
/// <b>Found by review, round 13.</b> The send read the box on the pool and wrote it on the loop, so a key typed between
/// the two was lost; and reading the box on the pool while the person typed threw <c>Collection was modified</c> into
/// the message line, where the person read it as a send that failed.
/// </para>
/// </remarks>
[Collection(nameof(OwnsTheMarshal))]
public sealed class TheBoxIsWrittenOnTheLoopTests
{
    private const string Next = " and then something else";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_from_the_pool_runs_on_the_thread_that_draws()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        using var loop = new StandInLoop();

        var writing = Task.Run(() => seated.Shell.Line.Written = "draft", Ct);

        while (!writing.IsCompleted)
        {
            loop.Pump();
        }

        await writing;

        Assert.Equal("draft", seated.Shell.Line.Written);
        Assert.Equal(1, loop.Ran);
    }

    [Fact]
    public async Task A_key_typed_while_the_sends_clear_waits_for_the_loop_is_kept()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        seated.Focus<LineRegion>();

        var gate = new TaskCompletionSource();
        shell.Queued(_ => gate.Task);

        shell.Line.Written = "yes";
        shell.Line.Accept();
        shell.Line.Written = "yes" + Next;

        using var loop = new StandInLoop();

        gate.SetResult();

        // The send has reached somebody, and its change to the box is waiting for the loop.
        Assert.True(loop.Waiting(TimeSpan.FromSeconds(20)), "the send never changed the box");

        // The loop takes input before what was handed to it, so a key typed now lands first.
        seated.Session.Type("X");

        var settled = shell.Settled;

        while (!settled.IsCompleted)
        {
            loop.Pump();
        }

        await settled;

        Assert.Contains("X", shell.Line.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_sent_while_the_person_keeps_typing_says_it_was_sent()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        seated.Focus<LineRegion>();

        var outcomes = new List<string>();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            shell.Line.Written = $"answer{attempt}";
            shell.Line.Accept();

            var settled = shell.Settled;

            using (var loop = new StandInLoop())
            {
                // The person goes on with their next thought while the send runs on the pool.
                while (!settled.IsCompleted)
                {
                    seated.Session.App.InjectKey(new Key('z'));
                    loop.Pump(wait: false);
                }
            }

            await settled;

            if (shell.Said.Contains("Collection was modified", StringComparison.Ordinal))
            {
                outcomes.Add($"attempt {attempt}: {shell.Said}");
            }

            shell.Line.Written = string.Empty;
        }

        Assert.True(outcomes.Count == 0, $"{outcomes.Count} of 20: {string.Join(" | ", outcomes.Take(3))}");
    }

    [Fact]
    public async Task Writing_a_long_line_while_the_conversation_is_followed_says_nothing_about_collections()
    {
        await using var seated = await Seated.OpenAsync(Ct, followEvery: TimeSpan.FromMilliseconds(10));
        var shell = seated.Shell;

        seated.Focus<LineRegion>();

        var said = new HashSet<string>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        // The follow reads the box on the pool to rebuild the draft, on its schedule, while somebody writes.
        using (var loop = new StandInLoop())
        {
            while (DateTime.UtcNow < deadline)
            {
                foreach (var character in "the deploy moves to noon because the freeze ends then ")
                {
                    seated.Session.App.InjectKey(new Key(character));
                    loop.Pump(wait: false);
                }

                said.Add(shell.Said);
            }

            shell.FollowEvery = null;

            var settled = shell.Settled;

            while (!settled.IsCompleted)
            {
                loop.Pump();
            }

            await settled;
        }

        said.Add(shell.Said);

        Assert.DoesNotContain(said, s => s.Contains("Collection was modified", StringComparison.Ordinal));
    }
}

/// <summary>The calling thread as the program's loop, set as <see cref="Prompting.Marshal"/> until disposed.</summary>
/// <remarks>
/// As the toolkit's <c>Invoke</c>: run at once when already on the loop's thread, and otherwise queued until the loop
/// is pumped. Queuing from the loop's own thread would wait on itself.
/// </remarks>
internal sealed class StandInLoop : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _ran;

    internal StandInLoop() => Prompting.Marshal = Invoke;

    /// <summary>How many things handed over from another thread have run.</summary>
    internal int Ran => Volatile.Read(ref _ran);

    /// <summary>Runs whatever was handed over.</summary>
    /// <param name="wait">Whether to wait briefly for something when nothing is there: not between keys, which
    /// would slow the typing the race needs.</param>
    internal void Pump(bool wait = true)
    {
        while (_queue.TryTake(out var act, wait ? TimeSpan.FromMilliseconds(5) : TimeSpan.Zero))
        {
            act();
            Interlocked.Increment(ref _ran);
        }
    }

    /// <summary>Whether something is handed over and waiting, within the time given.</summary>
    /// <param name="within">How long to wait.</param>
    /// <returns>True when something waits.</returns>
    internal bool Waiting(TimeSpan within)
    {
        var until = DateTime.UtcNow + within;

        while (_queue.Count == 0)
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    public void Dispose()
    {
        Prompting.Marshal = null;
        Pump();
        _queue.Dispose();
    }

    private void Invoke(Action act)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            act();

            return;
        }

        _queue.Add(act);
    }
}
