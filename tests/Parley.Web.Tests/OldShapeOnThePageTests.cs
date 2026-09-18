using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// A conversation written under 0.6 still reads on the console's own page.
/// </summary>
/// <remarks>
/// <para>
/// FR-009, on the surface a person is most likely to read an old conversation from. The client-side derivation
/// is checked in <c>Parley.Client.Tests</c>; what this adds is the half that lives in Razor — the page walks a
/// payload's points and its finish line, and a template that stopped rendering either would drop them silently,
/// because there is nothing for an exception to be thrown from.
/// </para>
/// <para>
/// Through the scripted broker rather than a store: what is under test is the page, and this project must not be
/// able to open a database (<c>GraphTests</c> walks it).
/// </para>
/// </remarks>
public sealed class OldShapeOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private const string OldRequest =
        """{"intent":"deciding where tax is added","items":[{"id":"r1","kind":"work","text":"use the order's price"},{"id":"r2","kind":"question","text":"which invoice version?"}],"done_when":"invoices show the order's price"}""";

    private const string OldResult =
        """{"outcomes":[{"id":"r1","outcome":"done","text":"merged in 4.2"},{"id":"r2","outcome":"answered","sources":["inventory@test"],"text":"schema 12"}]}""";

    [Fact]
    public async Task A_0_6_request_and_its_result_are_shown_with_every_field_they_carry()
    {
        using var console = new TranscriptConsole();
        console.Broker.Stored(1, Orders, Billing, Wire.MessageType.Request, OldRequest);
        console.Broker.Stored(2, Billing, Orders, Wire.MessageType.Result, OldResult);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        foreach (var shown in new[]
        {
            "deciding where tax is added",
            "use the order&#x27;s price",
            "which invoice version?",
            "invoices show the order&#x27;s price",
            "merged in 4.2",
            "schema 12",
            "inventory@test",
        })
        {
            Assert.Contains(shown, page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_0_7_request_is_shown_as_its_ask_with_no_empty_furniture_around_it()
    {
        // The complement. A 0.7 Request has no items and no finish line, so the page must render neither — an
        // empty list or a stray "Done when:" heading would be the old shape leaking through as decoration.
        using var console = new TranscriptConsole();
        console.Broker.Stored(
            1, Orders, Billing, Wire.MessageType.Request,
            """{"ask":"Can invoices use the price the order supplies?"}""");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("Can invoices use the price the order supplies?", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Done when:", page, StringComparison.Ordinal);
        Assert.DoesNotContain("&quot;ask&quot;", page, StringComparison.Ordinal);
    }
}
