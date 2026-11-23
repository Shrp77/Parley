using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

public sealed partial class TheInboxKeepsItsServerGuardsTests
{
    [Fact]
    public async Task The_waiting_on_you_count_is_of_the_rows_a_search_and_a_project_filter_keep()
    {
        // Found by review, lens 3 run I on 19cd7a08: the Waiting on you tab counted every question waiting on the person
        // whatever the search box or the project filter said, while its rows and the other two tabs' counts were filtered,
        // so the tab read 2 above one row, and nothing held it.
        using var console = new TwoProjects();

        Asked(console.Alpha, "alpha", 20, Orders, "Which invoice schema?");
        Asked(console.Beta, "beta", 21, Billing, "Which currency?");

        using var client = console.CreateClient();

        var every = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);
        var searched = await client.GetStringAsync(new Uri("/Inbox?q=invoice", UriKind.Relative), Ct);
        var scoped = await client.GetStringAsync(new Uri("/Inbox?scope=beta", UriKind.Relative), Ct);

        Assert.Contains("Waiting on you <span class=\"count\">2</span>", every, StringComparison.Ordinal);

        Assert.Contains("Which invoice schema?", searched, StringComparison.Ordinal);
        Assert.DoesNotContain("Which currency?", searched, StringComparison.Ordinal);
        Assert.Contains("Waiting on you <span class=\"count\">1</span>", searched, StringComparison.Ordinal);

        Assert.Contains("Which currency?", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("Which invoice schema?", scoped, StringComparison.Ordinal);
        Assert.Contains("Waiting on you <span class=\"count\">1</span>", scoped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_finds_a_closed_row_by_the_words_of_the_answer_that_closed_it()
    {
        // Found by review, lens 3 run I on 19cd7a08: a closed row's last message is the Result, whose words are its outcomes'
        // text, and the search could stop reading them with every test still passing. Those are the words a person remembers.
        using var console = new TwoProjects();

        Closed(console.Alpha, "alpha", 20, Billing, "Ship the ledger on Friday.", 30);
        Closed(console.Alpha, "alpha", 22, Orders, "Keep the old schema.", 30);

        using var client = console.CreateClient();

        var found = await client.GetStringAsync(new Uri("/Inbox?view=closed&q=friday", UriKind.Relative), Ct);

        Assert.Contains("Ship the ledger on Friday.", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep the old schema.", found, StringComparison.Ordinal);
        Assert.Contains("Closed <span class=\"count\">1</span>", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_finds_a_question_waiting_on_the_person_by_its_conversations_name()
    {
        // Found by review, lens 3 run I on 19cd7a08: Waiting on you's search could stop matching the conversation's name
        // with every test still passing, while In flight and Closed match it.
        using var console = new TwoProjects();

        Asked(console.Alpha, "alpha", 20, Orders, "Which schema?");
        Said(
            console.Alpha, 300, 30, Wire.MessageType.Request, Billing, PersonOf("alpha"),
            "{\"ask\":\"Which rate?\"}", 5, "ledger-rework");
        console.Alpha.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 30,
            State = Wire.ExchangeState.Open,
            OpenedUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
        console.Alpha.Edges.Add(new Wire.WaitingEdge { Waiter = Billing, WaitedOn = PersonOf("alpha"), ExchangeId = 30 });
        console.Alpha.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "ledger-rework",
            HasOpenExchange = true,
            LastActivityUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });

        using var client = console.CreateClient();

        var every = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);
        var found = await client.GetStringAsync(new Uri("/Inbox?q=rework", UriKind.Relative), Ct);

        Assert.Contains("Which schema?", every, StringComparison.Ordinal);
        Assert.Contains("Which rate?", every, StringComparison.Ordinal);
        Assert.Contains("Which rate?", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Which schema?", found, StringComparison.Ordinal);
    }
}
