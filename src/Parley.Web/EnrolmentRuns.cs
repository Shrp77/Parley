using System.Collections.Concurrent;

namespace Parley.Web;

/// <summary>What an enrolment run has said so far, and what became of it once it is over.</summary>
/// <param name="Done">Whether the run has finished.</param>
/// <param name="Activity">What each repository is doing, by role, in the order they were first mentioned.</param>
public sealed record EnrolmentProgress(bool Done, IReadOnlyList<KeyValuePair<string, string>> Activity);

/// <summary>
/// Enrolments that run beyond the request that asked for them, so the page can say what is happening while they do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a run outlives its request.</b> Each agent is started and waited for in turn, which takes the better part of
/// a minute for a handful. A request that does all of it says nothing until the end, and the only sign of life is the
/// browser's tab spinner. The command already reports what it is doing to each agent as it goes; this keeps that where
/// the page can ask for it.
/// </para>
/// <para>
/// <b>Held in memory, and forgotten.</b> A run is what one person did a moment ago and is read back by the page that
/// started it. Nothing here is a record: the registry is the record of who registered.
/// </para>
/// </remarks>
public sealed class EnrolmentRuns
{
    private const int Kept = 20;

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>What a finished run produced.</summary>
    /// <param name="Outcome">Who was enrolled and who registered.</param>
    /// <param name="Said">The command's own words, one per line, for a person to read.</param>
    public sealed record Result(Parley.Setup.EnrolmentOutcome Outcome, IReadOnlyList<string> Said);

    /// <summary>Begins a run.</summary>
    /// <param name="roles">The repositories being enrolled, each shown as waiting until the run reaches it.</param>
    /// <param name="work">The enrolment, handed the outcome to report into.</param>
    /// <returns>The run's identifier, to ask about it by.</returns>
    public string Start(IEnumerable<string> roles, Func<Parley.Setup.EnrolmentOutcome, Task<IReadOnlyList<string>>> work)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(work);

        var id = Guid.NewGuid().ToString("N");
        var run = new Run();

        foreach (var role in roles)
        {
            run.Say(role, "queued");
        }

        if (_runs.Count >= Kept)
        {
            // The oldest finished first, never in the dictionary's own order: that could drop the run whose page is about
            // to ask for its result, while older ones stayed (review, lens 1 run 67).
            foreach (var old in _runs.Where(pair => pair.Value.Finished)
                         .OrderBy(pair => pair.Value.FinishedAt)
                         .Select(pair => pair.Key)
                         .Take(_runs.Count - Kept + 1)
                         .ToList())
            {
                _ = _runs.TryRemove(old, out _);
            }
        }

        _runs[id] = run;

        _ = Task.Run(async () =>
        {
            var outcome = new Parley.Setup.EnrolmentOutcome(run.Say);

            try
            {
                run.Finish(new Result(outcome, await work(outcome).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                run.Finish(new Result(outcome, [$"Enrolment stopped: {ex.Message}"]));
            }
        });

        return id;
    }

    /// <summary>What a run has said so far.</summary>
    /// <param name="id">The run.</param>
    /// <returns>The progress, or null when there is no such run.</returns>
    public EnrolmentProgress? Progress(string? id) =>
        id is not null && _runs.TryGetValue(id, out var run) ? run.Progress() : null;

    /// <summary>What a finished run produced.</summary>
    /// <param name="id">The run.</param>
    /// <returns>The result, or null when there is no such run or it is not over.</returns>
    public Result? Finished(string? id) =>
        id is not null && _runs.TryGetValue(id, out var run) ? run.Outcome : null;

    private sealed class Run
    {
        private readonly object _gate = new();
        private readonly List<KeyValuePair<string, string>> _activity = [];

        public Result? Outcome { get; private set; }

        public bool Finished => Outcome is not null;

        public long FinishedAt { get; private set; }

        public void Say(string role, string activity)
        {
            lock (_gate)
            {
                var at = _activity.FindIndex(pair => pair.Key == role);

                if (at < 0)
                {
                    _activity.Add(new(role, activity));
                }
                else
                {
                    _activity[at] = new(role, activity);
                }
            }
        }

        public void Finish(Result result)
        {
            lock (_gate)
            {
                FinishedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                Outcome = result;
            }
        }

        public EnrolmentProgress Progress()
        {
            lock (_gate)
            {
                return new EnrolmentProgress(Outcome is not null, [.. _activity]);
            }
        }
    }
}
