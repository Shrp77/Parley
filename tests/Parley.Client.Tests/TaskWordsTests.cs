using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>How a surface lists the background tasks a count says are running (SPEC.md §11.6).</summary>
/// <remarks>
/// The count and the named list come from the broker separately. A surface lists as many tasks as either says, so a task
/// the node named is never dropped because the count it travelled with was lower.
/// </remarks>
[Unit]
public sealed class TaskWordsTests
{
    [Fact]
    public void A_count_lower_than_the_named_list_still_lists_every_named_task()
    {
        var named = new[]
        {
            new BackgroundTask("t1", TaskKind.Agent, "Checking VAT"),
            new BackgroundTask("t2", TaskKind.Command, "Running the tests"),
            new BackgroundTask("t3", TaskKind.Agent, "Reading the rules"),
        };

        var every = TaskWords.Every(named, 1);

        Assert.Equal(["t1", "t2", "t3"], every.Select(t => t!.Id));
    }

    [Fact]
    public void A_count_higher_than_the_named_list_is_made_up_with_stand_ins()
    {
        var every = TaskWords.Every([new BackgroundTask("t1", TaskKind.Agent, "Checking VAT")], 3);

        Assert.Equal(3, every.Count);
        Assert.Equal("t1", every[0]!.Id);
        Assert.Null(every[1]);
        Assert.Null(every[2]);
    }
}
