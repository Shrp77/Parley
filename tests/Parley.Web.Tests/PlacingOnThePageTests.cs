using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The person placing a file from the Files page (SPEC.md §10.2 <c>place_file</c>): the form, what reaches the broker,
/// and what the page says came of it.
/// </summary>
/// <remarks>
/// Each test posts the form a person would, as multipart with the page's own token, and reads the page that comes back
/// — the words a person reads, not a field of the model.
/// </remarks>
public sealed class PlacingOnThePageTests : IDisposable
{
    private const string Page = "/Projects/Files?project=" + FilesConsole.Project;

    private readonly FilesConsole _console = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task The_page_offers_a_form_naming_every_conversation_the_project_lists()
    {
        _console.Broker.Conversations.Add("rates");

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains("enctype=\"multipart/form-data\"", body, StringComparison.Ordinal);
        Assert.Contains("<option value=\"pricing\">pricing</option>", body, StringComparison.Ordinal);
        Assert.Contains("<option value=\"rates\">rates</option>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_scoped_to_a_conversation_offers_only_that_one()
    {
        _console.Broker.Conversations.Add("rates");

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page + "&conversation=rates", UriKind.Relative), Ct);

        Assert.Contains("<option value=\"rates\">rates</option>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"pricing\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_posted_is_streamed_to_the_broker_as_the_persons_and_the_page_says_it_was_placed()
    {
        using var client = _console.CreateClient();

        var (status, body) = await PlaceAsync(client, "notes.md", "net of tax", "pricing", placeAs: null);

        Assert.Equal(HttpStatusCode.OK, status);

        var (header, bytes) = Assert.Single(_console.Broker.Placed);
        Assert.Equal("pricing", header.ConversationId);
        Assert.Equal("notes.md", header.Name);
        Assert.Equal(string.Empty, header.Agent);
        Assert.Equal("net of tax", Encoding.UTF8.GetString(bytes));

        Assert.Contains(
            "Placed notes.md in pricing: 10 B, sha256 " + ContentHash.Of("net of tax"u8).Short + ".",
            Status(body),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_given_is_the_name_placed()
    {
        using var client = _console.CreateClient();

        await PlaceAsync(client, "draft.md", "x", "pricing", placeAs: "final.md");

        Assert.Equal("final.md", Assert.Single(_console.Broker.Placed).Header.Name);
    }

    [Fact]
    public async Task A_refusal_comes_back_in_the_brokers_words()
    {
        _console.Broker.Placing = (header, _) => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.HoldsOtherContent,
            Rejection = Wire.FileRejection.NameHoldsOtherContent,
            ConversationId = header.ConversationId,
            Name = header.Name,
            Detail = "notes.md already holds other content. A new version goes under a new name."
        };

        using var client = _console.CreateClient();

        var (_, body) = await PlaceAsync(client, "notes.md", "changed", "pricing", placeAs: null);

        Assert.Equal(
            "Not placed: notes.md already holds other content. A new version goes under a new name.",
            Status(body));
    }

    [Fact]
    public async Task A_conversation_the_page_did_not_offer_is_refused_and_nothing_reaches_the_broker()
    {
        using var client = _console.CreateClient();

        var (_, body) = await PlaceAsync(client, "notes.md", "x", "elsewhere", placeAs: null);

        Assert.Empty(_console.Broker.Placed);
        Assert.Contains("Nothing was placed.", Status(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_without_the_pages_token_is_refused_and_nothing_is_placed()
    {
        using var client = _console.CreateClient();

        var (status, _) = await PlaceAsync(client, "notes.md", "x", "pricing", placeAs: null, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_console.Broker.Placed);
    }

    [Fact]
    public void The_page_bounds_an_upload_explicitly()
    {
        var limit = typeof(Parley.Web.Pages.Projects.FilesModel)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), inherit: false);

        Assert.Single(limit);
        Assert.Equal(256L * 1024 * 1024, Parley.Web.Pages.Projects.FilesModel.PlaceLimitBytes);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PlaceAsync(
        HttpClient client, string fileName, string text, string placeIn, string? placeAs, bool withToken = true)
    {
        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(FilesConsole.Project), "project");
        form.Add(new StringContent(placeIn), "placeIn");

        if (placeAs is not null)
        {
            form.Add(new StringContent(placeAs), "placeAs");
        }

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "upload", fileName);

        if (withToken)
        {
            var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

            var token = Regex.Match(
                page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

            Assert.NotEmpty(token);

            form.Add(new StringContent(token), "__RequestVerificationToken");
        }

        using var response = await client.PostAsync(new Uri("/Projects/Files?handler=Place", UriKind.Relative), form, Ct);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Status(string body)
    {
        var status = Regex.Match(
            body, """<p role="status" class="outcome">(.*?)</p>""", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        return status.Success ? WebUtility.HtmlDecode(status.Groups[1].Value) : "(no outcome on the page)";
    }
}
