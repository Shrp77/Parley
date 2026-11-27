using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>How much of background tasks one registry reply carries (SPEC.md §11.6).</summary>
/// <remarks>
/// Every agent's row is in one reply, and a reply past gRPC's limit is refused whole, which would hide every agent from the
/// person. So tasks' words are spent from a budget; a task whose words do not fit still travels by id and kind, and the
/// count and the list still agree.
/// </remarks>
[Unit]
public sealed class TaskWordBudgetTests
{
    private static BackgroundTask Task(string id, int words) => new(id, TaskKind.Agent, new string('n', words));

    [Fact]
    public void What_fits_is_kept_whole()
    {
        var budget = new TaskWordBudget(1000);

        var kept = budget.Spend([Task("a", 10), Task("b", 10)]);

        Assert.Equal(["a", "b"], kept.Select(t => t.Id));
        Assert.All(kept, t => Assert.Equal(10, t.Name.Length));
    }

    [Fact]
    public void A_task_whose_words_do_not_fit_travels_by_id_and_later_ones_that_fit_keep_theirs()
    {
        // Room for the ids and framing of all three, and the words of the small ones.
        var budget = new TaskWordBudget((3 * (1 + TaskWordBudget.Framing)) + 20 + 500);

        var kept = budget.Spend([Task("a", 10), Task("b", 600), Task("c", 10)]);

        Assert.Equal(["a", "b", "c"], kept.Select(t => t.Id));
        Assert.Equal([10, 0, 10], kept.Select(t => t.Name.Length));
    }

    [Fact]
    public void The_budget_is_shared_across_every_row_it_is_spent_on()
    {
        // Eight rows' worth of shares, each row taking its whole share: the ninth finds nothing left.
        var share = 100;
        var budget = new TaskWordBudget((share * TaskWordBudget.Shares) + (9 * (1 + TaskWordBudget.Framing)));

        var rows = Enumerable.Range(0, 9).Select(i => budget.Spend([Task(i.ToString(System.Globalization.CultureInfo.InvariantCulture), share)])).ToList();

        Assert.All(rows.Take(8), row => Assert.Equal(share, Assert.Single(row).Name.Length));
        Assert.Equal(string.Empty, Assert.Single(rows[8]).Name);
    }

    [Fact]
    public void Words_that_spend_the_budget_to_exactly_nothing_are_kept()
    {
        // Eight rows, each one task of a hundred bytes of words and its id: the last row's words are exactly what is left.
        var share = 100;
        var budget = new TaskWordBudget(TaskWordBudget.Shares * (share + 1 + TaskWordBudget.Framing));

        var rows = Enumerable.Range(0, TaskWordBudget.Shares).Select(i => budget.Spend([Task(i.ToString(System.Globalization.CultureInfo.InvariantCulture), share)])).ToList();

        Assert.All(rows, row => Assert.Equal(share, Assert.Single(row).Name.Length));
        Assert.Equal(string.Empty, Assert.Single(budget.Spend([Task("z", 1)])).Name);
    }

    [Fact]
    public void No_row_takes_more_than_its_share_however_much_is_left()
    {
        var budget = new TaskWordBudget(8000);

        var greedy = budget.Spend([.. Enumerable.Range(0, 20).Select(i => Task($"g{i}", 100))]);
        var modest = budget.Spend([Task("m", 100)]);

        // A share is a thousand bytes: ten of the greedy row's tasks fit in it, and the next row keeps its words.
        Assert.Equal(10, greedy.Count(t => t.Name.Length > 0));
        Assert.Equal(100, Assert.Single(modest).Name.Length);
    }

    [Fact]
    public void The_reply_budget_leaves_room_under_grpcs_limit_for_the_largest_rows()
    {
        // Every word at its bound, every task named, for many agents: the words never pass the budget, and the budget is
        // a quarter of the 4 MiB a reply may be.
        var budget = new TaskWordBudget(OperatorsService.MostTaskWordBytes);
        var task = new BackgroundTask(
            new string('i', BackgroundTaskText.LongestId),
            TaskKind.Agent,
            new string((char)0x4E00, BackgroundTaskText.LongestName),
            new string((char)0x4E00, BackgroundTaskText.LongestDescription));
        var spent = 0L;

        for (var agent = 0; agent < 50; agent++)
        {
            foreach (var kept in budget.Spend([.. Enumerable.Repeat(task, BackgroundTaskText.MostTasks)]))
            {
                spent += System.Text.Encoding.UTF8.GetByteCount(kept.Id + kept.Name + kept.Description) + TaskWordBudget.Framing;
            }
        }

        Assert.True(spent < 4L * 1024 * 1024 / 2, $"{spent} bytes");
    }
}
