using Microsoft.Extensions.DependencyInjection;
using Parley.Web.Notifications;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The bell beside the person's name: what the console told them, kept so it can be read again.
/// </summary>
public sealed class NotificationBellTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Said =
        "Sent. Exchange 55 is open in conversation rm1-5, and orderbook@workstation owes a Result.";

    [Fact]
    public async Task A_message_the_console_reported_is_in_the_bell_with_a_count_and_a_way_to_its_conversation()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        console.Services.GetRequiredService<NotificationLog>()
            .Add(TranscriptConsole.Project, "rm1-5", "orderbook@workstation", Said, good: true);

        var page = await client.GetStringAsync(new Uri($"/Projects/Agents?project={TranscriptConsole.Project}", UriKind.Relative), Ct);

        Assert.Contains("class=\"bell\"", page, StringComparison.Ordinal);
        Assert.Contains("Notifications, 1 new", page, StringComparison.Ordinal);
        Assert.Contains("Exchange 55 is open in conversation rm1-5", page, StringComparison.Ordinal);
        Assert.Contains($"/Conversations/Detail?project={TranscriptConsole.Project}&amp;id=rm1-5", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/Notifications\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_the_list_shows_everything_and_clears_the_count()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var log = console.Services.GetRequiredService<NotificationLog>();

        log.Add(TranscriptConsole.Project, "rm1-5", "orderbook@workstation", Said, good: true);
        log.Add(TranscriptConsole.Project, "rm1-5", "billing@alpha", "The broker refused it: no such agent.", good: false);

        var list = await client.GetStringAsync(new Uri("/Notifications", UriKind.Relative), Ct);

        Assert.Contains("Exchange 55 is open", list, StringComparison.Ordinal);
        Assert.Contains("The broker refused it", list, StringComparison.Ordinal);
        Assert.Contains("not sent", list, StringComparison.Ordinal);
        Assert.Equal(0, log.Unread());

        var after = await client.GetStringAsync(new Uri($"/Projects/Agents?project={TranscriptConsole.Project}", UriKind.Relative), Ct);

        Assert.DoesNotContain("Notifications, ", after, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_keeps_the_newest_and_drops_the_oldest_past_its_bound()
    {
        var log = new NotificationLog();

        for (var i = 1; i <= NotificationLog.Capacity + 5; i++)
        {
            log.Add("alpha", null, "orders@alpha", $"n{i}", good: true);
        }

        var all = log.All();

        Assert.Equal(NotificationLog.Capacity, all.Count);
        Assert.Equal($"n{NotificationLog.Capacity + 5}", all[0].Text);
        Assert.Equal("n6", all[^1].Text);
    }
}
