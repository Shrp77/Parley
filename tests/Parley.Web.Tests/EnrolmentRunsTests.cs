using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That an enrolment can be watched while it runs, and read once it is over.
/// </summary>
/// <remarks>
/// A run that did all its work inside the request said nothing for the better part of a minute, and the browser's tab
/// spinner was the only sign anything was happening. The page asks a run what each repository is doing, so a run has to
/// show every repository from the start and show it finished only when it is.
/// </remarks>
[Integration]
public sealed class EnrolmentRunsTests
{
    [Fact]
    public async Task A_run_shows_every_repository_before_it_has_said_anything_and_is_not_done_until_it_is()
    {
        var runs = new EnrolmentRuns();
        var release = new TaskCompletionSource();

        var id = runs.Start(["orders", "billing"], async outcome =>
        {
            await release.Task;

            return ["said"];
        });

        var during = runs.Progress(id);

        Assert.NotNull(during);
        Assert.False(during.Done);
        Assert.Equal(["orders", "billing"], during.Activity.Select(pair => pair.Key));
        Assert.Null(runs.Finished(id));

        release.SetResult();

        EnrolmentRuns.Result? finished = null;

        for (var wait = 0; wait < 100 && finished is null; wait++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            finished = runs.Finished(id);
        }

        Assert.NotNull(finished);
        Assert.Equal(["said"], finished.Said);
        Assert.True(runs.Progress(id)!.Done);
    }

    [Fact]
    public async Task A_run_that_throws_says_so_rather_than_staying_busy()
    {
        var runs = new EnrolmentRuns();
        var id = runs.Start(["orders"], _ => throw new InvalidOperationException("no catalogue"));

        EnrolmentRuns.Result? finished = null;

        for (var wait = 0; wait < 100 && finished is null; wait++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            finished = runs.Finished(id);
        }

        Assert.NotNull(finished);
        Assert.Contains("no catalogue", string.Join(' ', finished.Said), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_list_lets_the_oldest_finished_run_go_never_the_newest()
    {
        // Review, lens 1 run 67: runs were dropped in the dictionary's own order, so starting one more could drop the run
        // whose page was about to ask for its result (10 of 200 trials) while older ones stayed.
        var runs = new EnrolmentRuns();
        var ids = new List<string>();

        for (var i = 0; i < 20; i++)
        {
            var id = runs.Start(["orders"], _ => Task.FromResult<IReadOnlyList<string>>([]));
            ids.Add(id);

            for (var wait = 0; wait < 100 && runs.Finished(id) is null; wait++)
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        _ = runs.Start(["billing"], _ => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Null(runs.Finished(ids[0]));
        Assert.NotNull(runs.Finished(ids[^1]));
    }

    [Fact]
    public void An_unknown_run_is_not_a_finished_one()
    {
        var runs = new EnrolmentRuns();

        Assert.Null(runs.Progress("nothing"));
        Assert.Null(runs.Finished("nothing"));
        Assert.Null(runs.Progress(null));
    }
}
