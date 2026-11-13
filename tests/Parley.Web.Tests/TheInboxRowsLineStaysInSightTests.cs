using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The Inbox's grid has no column for what was said, and an open row's line stays in sight while it is read.</summary>
/// <remarks>
/// <b>The owner, 2026-11-13:</b> "remove the Message column - it ALWAYS overflows and doesn't really help", and keep the
/// From, Type, Project and Conversation line visible while the entry's contents scroll, so it is clear who the person is
/// writing to and the row can be closed without scrolling back.
/// </remarks>
[Integration]
public sealed class TheInboxRowsLineStaysInSightTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Waiting_has_no_message_column_and_the_words_are_only_in_the_opened_row()
    {
        using var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Person,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "shared", HasOpenExchange = true });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 1,
            ExchangeId = 1,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = "{\"ask\":" + JsonSerializer.Serialize("Which schema do we keep?") + "}",
            ConversationId = "shared",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 1,
            State = Wire.ExchangeState.Open,
            OpenedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        using var client = console.CreateClient();
        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        var head = Regex.Match(page, "<div class=\"dgrid-head\" role=\"row\">(.*?)</div>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var columns = Regex.Matches(head, "<span role=\"columnheader\">([^<]*)</span>", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(["From", "Type", "Project", "Conversation", "When", "Status"], columns);

        var line = Regex.Match(page, "<summary class=\"dgrid-line\">(.*?)</summary>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.Equal(columns.Count, Regex.Matches(line, "<span role=\"cell\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
        Assert.DoesNotContain("Which schema do we keep?", line, StringComparison.Ordinal);
        Assert.Contains("Which schema do we keep?", page, StringComparison.Ordinal);
    }

    [Fact]
    public void In_flight_and_closed_have_no_last_said_column_and_a_row_and_its_columns_agree()
    {
        var rows = ScriptSource.Page("src/Parley.Web/Pages/_InboxRows.cshtml");

        Assert.DoesNotContain("Last said", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview(", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview(", ScriptSource.Page("src/Parley.Web/Pages/Inbox.cshtml"), StringComparison.Ordinal);

        // As many columns as headers, or every cell after the gap lands under the wrong heading.
        var cols = Regex.Match(rows, "--cols: ([^;]*);", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
        var tracks = Regex.Matches(cols, @"minmax\([^)]*\)|\d+(?:\.\d+)?rem", RegexOptions.None, TimeSpan.FromSeconds(2)).Count;

        Assert.Equal(6, tracks);
        Assert.Equal(6, Regex.Matches(rows, "role=\"columnheader\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
    }

    [Fact]
    public void An_open_rows_line_sticks_under_the_header_on_an_opaque_background()
    {
        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

        Assert.Contains(
            "details.dgrid-row[open] > summary { position: sticky; top: var(--dgrid-head-h); z-index: 1;",
            layout,
            StringComparison.Ordinal);

        // A translucent background let the words scrolling under the line show through it.
        Assert.Contains("transparent) 0 0), var(--card);", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("details.dgrid-row[open] > summary { background: color-mix(", layout, StringComparison.Ordinal);

        ScriptSource.Once(
            ScriptSource.Code(layout, "--dgrid-head-h"),
            "if (head) { grid.style.setProperty(\"--dgrid-head-h\", head.offsetHeight + \"px\"); }",
            "the header is no longer measured, so the line sticks at a guessed height");
    }
}
