using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That acting on several conversations from the list does what its buttons say, to no more than it was given, and ends
/// nothing until the word is typed.
/// </summary>
/// <remarks>
/// Every case posts the real form to the real page and counts what the broker was asked. These were once checks of the
/// source text, and review of 0.64.3 turned the typed word off, removed the cap and swapped archive with bring-back, all
/// at once, with every one of them still passing.
/// </remarks>
[Integration]
public sealed partial class BulkConversationActsTests
{
    private const string Archive = "/parley.v1.Operators/Archive";
    private const string Unarchive = "/parley.v1.Operators/Unarchive";
    private const string ForceConclude = "/parley.v1.Operators/ForceConclude";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    /// <summary>A console whose project has one open, one closed and one closed-and-archived conversation.</summary>
    private static RoadmapConsole Console()
    {
        var console = new RoadmapConsole();

        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "open", HasOpenExchange = true });
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "closed", Closed = true });
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "filed", Closed = true, Archived = true });

        return console;
    }

    private static int Asked(RoadmapConsole console, string path) => console.Broker.Paths.Count(p => p == path);

    private static async Task<HttpResponseMessage> PostAsync(
        RoadmapConsole console, string address, IEnumerable<KeyValuePair<string, string>> fields)
    {
        using var client = console.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}&showArchived=true", UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("project", RoadmapConsole.Project),
            .. fields,
        ]);

        return await client.PostAsync(new Uri(address, UriKind.Relative), form, Ct);
    }

    private static Task<HttpResponseMessage> BulkAsync(RoadmapConsole console, string op, params string[] ids) =>
        PostAsync(
            console,
            $"/Projects/Agents?project={RoadmapConsole.Project}&handler=Bulk",
            [new("op", op), .. ids.Select(id => new KeyValuePair<string, string>("ids", id))]);

    private static Task<HttpResponseMessage> EndAsync(RoadmapConsole console, string? typed, params string[] ids) =>
        PostAsync(
            console,
            "/Projects/EndMany",
            [
                .. ids.Select(id => new KeyValuePair<string, string>("Ids", id)),
                .. typed is null ? Array.Empty<KeyValuePair<string, string>>() : [new("Typed", typed)],
            ]);

    [Fact]
    public async Task Archiving_the_ticked_asks_the_broker_to_archive_each_once_and_to_bring_none_back()
    {
        using var console = Console();

        using var response = await BulkAsync(console, "archive", "open", "closed", "closed", " ");

        Assert.Equal(2, Asked(console, Archive));
        Assert.Equal(0, Asked(console, Unarchive));
        Assert.Equal(0, Asked(console, ForceConclude));
    }

    [Fact]
    public async Task Bringing_back_the_ticked_asks_the_broker_to_bring_each_back_and_to_archive_none()
    {
        using var console = Console();

        using var response = await BulkAsync(console, "unarchive", "filed");

        Assert.Equal(1, Asked(console, Unarchive));
        Assert.Equal(0, Asked(console, Archive));
    }

    [Fact]
    public async Task Archiving_all_closed_takes_only_what_the_broker_says_is_closed_and_not_yet_archived_whatever_is_ticked()
    {
        using var console = Console();

        using var response = await BulkAsync(console, "archiveclosed", "open");

        Assert.Equal(1, Asked(console, Archive));
        Assert.Equal(0, Asked(console, Unarchive));
    }

    [Fact]
    public async Task Archiving_all_closed_never_takes_a_closed_conversation_that_holds_open_work()
    {
        // A conversation a person ended can still hold an exchange somebody owes. Filing it would take that work off the list.
        using var console = Console();

        console.Broker.Conversations.Add(
            new Wire.ConversationSummary { ConversationId = "closed-owing", Closed = true, HasOpenExchange = true });

        using var response = await BulkAsync(console, "archiveclosed");

        // Only the fixture's one closed conversation that holds nothing.
        Assert.Equal(1, Asked(console, Archive));
        Assert.Equal(0, Asked(console, Unarchive));
    }

    [Fact]
    public async Task A_bulk_act_changes_no_more_than_its_bound_however_many_are_ticked()
    {
        using var console = Console();

        using var response = await BulkAsync(console, "archive", [.. Enumerable.Range(0, 600).Select(i => $"c{i}")]);

        Assert.Equal(500, Asked(console, Archive));
    }

    [Theory]
    [InlineData("end")]
    [InlineData("ARCHIVE")]
    [InlineData("destroy")]
    [InlineData("")]
    public async Task An_operation_the_list_does_not_have_changes_nothing(string op)
    {
        using var console = Console();

        using var response = await BulkAsync(console, op, "open", "closed");

        Assert.Equal(0, Asked(console, Archive));
        Assert.Equal(0, Asked(console, Unarchive));
        Assert.Equal(0, Asked(console, ForceConclude));
    }

    [Fact]
    public async Task A_bulk_act_says_what_it_did_and_keeps_the_listing_as_it_was()
    {
        using var console = Console();

        using var response = await PostAsync(
            console,
            $"/Projects/Agents?project={RoadmapConsole.Project}&handler=Bulk",
            [new("op", "unarchive"), new("ids", "filed"), new("showArchived", "true"), new("sort", "name")]);

        var back = response.Headers.Location!.ToString();

        Assert.Contains("did=restored", back, StringComparison.Ordinal);
        Assert.Contains("showArchived=True", back, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sort=name", back, StringComparison.Ordinal);
        Assert.Contains("filed=", back, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("END")]
    [InlineData("ending")]
    [InlineData("yes")]
    public async Task Ending_lists_what_would_end_and_ends_nothing_until_the_word_is_typed(string? typed)
    {
        using var console = Console();

        using var response = await EndAsync(console, typed, "open", "closed");
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(0, Asked(console, ForceConclude));
        Assert.Contains("name=\"Typed\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"open\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"closed\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ending_with_the_word_typed_ends_each_chosen_conversation_once_and_no_more_than_its_bound()
    {
        using var console = Console();

        using (await EndAsync(console, "end", "open", "closed", "open", " "))
        {
        }

        Assert.Equal(2, Asked(console, ForceConclude));

        using var many = Console();

        using (await EndAsync(many, " end ", [.. Enumerable.Range(0, 150).Select(i => $"c{i}")]))
        {
        }

        Assert.Equal(100, Asked(many, ForceConclude));
    }

    [Fact]
    public async Task The_list_offers_a_tick_on_each_row_and_sends_ending_to_its_own_page()
    {
        using var console = Console();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri($"/Projects/Agents?project={RoadmapConsole.Project}&showArchived=true", UriKind.Relative), Ct);

        Assert.Contains("form=\"bulk\" name=\"ids\" value=\"open\"", page, StringComparison.Ordinal);
        Assert.Contains("form=\"bulk\" name=\"ids\" value=\"filed\"", page, StringComparison.Ordinal);
        Assert.Contains("formaction=\"/Projects/EndMany\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"archiveclosed\"", page, StringComparison.Ordinal);
    }
}
