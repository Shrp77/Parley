using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// A Request and its Result read on the console's own page.
/// </summary>
/// <remarks>
/// <para>
/// The client-side derivation is checked in <c>Parley.Client.Tests</c>; what this adds is the half that lives in
/// Razor, where a template that stopped rendering a field would drop it silently.
/// </para>
/// <para>
/// Through the scripted broker rather than a store: what is under test is the page, and this project must not be
/// able to open a database (<c>GraphTests</c> walks it).
/// </para>
/// </remarks>
[Integration]
public sealed class AskOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    [Fact]
    public async Task A_request_is_shown_as_its_ask_and_its_result_with_every_outcome()
    {
        using var console = new TranscriptConsole();
        console.Broker.Stored(
            1, Orders, Billing, Wire.MessageType.Request,
            """{"ask":"Can invoices use the price the order supplies?"}""");
        console.Broker.Stored(
            2, Billing, Orders, Wire.MessageType.Result,
            """{"outcomes":[{"id":"request","outcome":"answered","sources":["inventory@test"],"text":"schema 12"}]}""");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        foreach (var shown in new[]
        {
            "Can invoices use the price the order supplies?",
            "schema 12",
            "inventory@test",
        })
        {
            Assert.Contains(shown, page, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("&quot;ask&quot;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_outcome_says_what_blocked_it()
    {
        // Without the blocker beside the verdict, a blocked Result reads "blocked" and the person cannot tell missing
        // information from a refused permission (review, lens 3 round 33).
        var page = await ResultPageAsync(
            """{"outcomes":[{"id":"request","outcome":"blocked","blocker":"missing_information","text":"Which year?"}]}""");

        Assert.Contains("<span class=\"verdict\">blocked: missing information</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outcome_shows_the_item_it_answers()
    {
        // Without the label, a Result with several outcomes no longer says which item each answers (review, lens 3
        // round 33).
        var page = await ResultPageAsync(
            """{"outcomes":[{"id":"rates-item","outcome":"done","text":"Switched on."}]}""");

        Assert.Contains("<span class=\"label\">rates-item</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_of_no_known_shape_is_shown_whole()
    {
        // Without the raw view, a message the console cannot read is drawn as nothing, as if nobody had said anything
        // (review, lens 3 round 33).
        var page = await ResultPageAsync("""{"answer":"forty-two"}""");

        Assert.Contains("<pre>{&quot;answer&quot;:&quot;forty-two&quot;}</pre>", page, StringComparison.Ordinal);
    }

    private static async Task<string> ResultPageAsync(string result)
    {
        using var console = new TranscriptConsole();
        console.Broker.Stored(1, Orders, Billing, Wire.MessageType.Request, """{"ask":"Can invoices use the order's price?"}""");
        console.Broker.Stored(2, Billing, Orders, Wire.MessageType.Result, result);
        using var client = console.CreateClient();

        return await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
    }
}
