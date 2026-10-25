using System.Collections.Concurrent;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>Runs alone: its tests set <see cref="Prompting.Marshal"/>, which every shell in the process reads.</summary>
[CollectionDefinition(nameof(OwnsTheMarshal), DisableParallelization = true)]
public sealed class OwnsTheMarshal;

/// <summary>The calling thread as the program's loop, set as <see cref="Prompting.Marshal"/> until disposed.</summary>
/// <remarks>
/// As the toolkit's <c>Invoke</c>: run at once when already on the loop's thread, and otherwise queued until the loop
/// is pumped. Queuing from the loop's own thread would wait on itself.
/// </remarks>
internal sealed class StandInLoop : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    // The thread itself, not its managed id: an id is handed to a new thread once the old one is collected, and a pool
    // thread that inherited the made-elsewhere loop's id would count as the loop and run its work in time.
    private readonly Thread _thread = Thread.CurrentThread;
    private int _ran;

    internal StandInLoop() => Prompting.Marshal = Invoke;

    /// <summary>A loop made on a thread that then runs nothing, so nothing handed over runs until pumped.</summary>
    /// <returns>The loop.</returns>
    /// <remarks>
    /// For a loop that is late. Made on the test's thread, a work queue continuation that happened to run there would
    /// count as on the loop and run inline, in time; but then nothing the test itself writes may go through the loop,
    /// because the test's thread is not it.
    /// </remarks>
    internal static StandInLoop Elsewhere()
    {
        StandInLoop? made = null;
        var making = new Thread(() => made = new StandInLoop());

        making.Start();
        making.Join();

        return made!;
    }

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
        if (ReferenceEquals(Thread.CurrentThread, _thread))
        {
            act();

            return;
        }

        _queue.Add(act);
    }
}
