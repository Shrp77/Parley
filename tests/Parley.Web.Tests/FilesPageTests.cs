using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The Files page: what it renders from the broker's answer, what it carries back, and that nothing on it deletes or
/// launches without a description first and this page's own token (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// Every refusal is checked by what the broker was asked and what was launched, which must be nothing — a page that
/// said "Nothing was removed." having asked for a removal is the failure that matters.
/// </remarks>
[Integration]
public sealed class FilesPageTests : IDisposable
{
    private const string Page = "/Projects/Files?project=" + FilesConsole.Project;

    private static readonly string Hash = new('a', 64);

    private readonly FilesConsole _console = new();

    public FilesPageTests()
    {
        _console.Broker.Files.Add(View("pricing", "notes.md", 10, Hash, "billing@alpha"));
        _console.Broker.Files.Add(View("pricing", "copied.md", 5, new string('b', 64), string.Empty));
        _console.Broker.Placements.Add(new Wire.FilePlacementCount { Agent = "billing@alpha", Placements = 3, Bytes = 30, LastUtc = "2026-09-14T09:00:00Z" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task The_place_to_put_a_file_says_what_a_conversation_was_spun_off_from_and_posts_only_its_name()
    {
        _console.Broker.Parent = "logging";

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains(
            $"<option value=\"pricing\">{System.Net.WebUtility.HtmlEncode(Parley.Client.Rendering.ConversationWording.Listed("pricing", "logging"))}</option>",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_shows_each_file_with_its_size_short_hash_and_placer_and_the_placements_under_it()
    {
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains("notes.md", body, StringComparison.Ordinal);
        Assert.Contains("10 B", body, StringComparison.Ordinal);
        Assert.Contains(ContentHash.Parse(Hash).Short, body, StringComparison.Ordinal);
        Assert.Contains("billing@alpha", body, StringComparison.Ordinal);
        Assert.Contains("the person", body, StringComparison.Ordinal);
        Assert.Contains("2 file(s), 15 B in all.", body, StringComparison.Ordinal);
        Assert.Contains("billing@alpha: 3 placement(s), 30 B", body, StringComparison.Ordinal);

        // Review gate, lens 3 run 3.3: every other time this console shows is local, so these are too, and it says so.
        var changed = DateTimeOffset.Parse("2026-09-14T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(Parley.Client.Rendering.LocalTime.Write(changed), body, StringComparison.Ordinal);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Parley.Client.Rendering.LocalTime.Note()), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversations_are_encoded_as_somebody_elses_text()
    {
        // A name can no longer carry markup: SPEC.md §9.5 makes it letters, digits, '.', '_' and '-', and the client
        // refuses a listing that breaks that rather than showing it. The conversation arriving from a broker is still
        // text this surface did not write.
        _console.Broker.Files.Add(View("pricing<b>", "Tom_and_Jerry.md", 1, new string('c', 64), "billing@alpha"));

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.DoesNotContain("pricing<b>", body, StringComparison.Ordinal);
        Assert.Contains("pricing&lt;b&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_and_a_page_token_reach_the_broker_and_a_next_page_is_linked()
    {
        _console.Broker.NextPageToken = "k/1+";

        using var client = _console.CreateClient();

        var first = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains("More files", first, StringComparison.Ordinal);
        Assert.Contains("pageToken=k%2F1%2B", first, StringComparison.Ordinal);

        await client.GetStringAsync(new Uri(Page + "&conversation=pricing&pageToken=k%2F1%2B", UriKind.Relative), Ct);

        var asked = _console.Broker.Listed[^1];
        Assert.Equal("pricing", asked.ConversationId);
        Assert.Equal("k/1+", asked.PageToken);
        Assert.InRange(asked.PageSize, 0, 50);
    }

    [Fact]
    public async Task Removing_describes_the_files_first_and_removes_only_when_pressed_again()
    {
        using var client = _console.CreateClient();

        var (describedStatus, described) = await PostAsync(client, "Remove", [("selected", "pricing/notes.md")]);

        Assert.Equal(HttpStatusCode.OK, describedStatus);
        Assert.Contains("whichever agent placed them", described, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", described, StringComparison.Ordinal);
        Assert.Empty(_console.Broker.Removed);

        var (_, removed) = await PostAsync(client, "Remove", [("selected", "pricing/notes.md"), ("confirmed", "yes")]);

        var asked = Assert.Single(Assert.Single(_console.Broker.Removed).Files);
        Assert.Equal("pricing", asked.ConversationId);
        Assert.Equal("notes.md", asked.Name);
        var said = Status(removed);
        Assert.True(said.Contains("Removed 1 file(s), freeing 10 B.", StringComparison.Ordinal), said);
    }

    [Fact]
    public async Task Pruning_takes_the_projects_name_and_nothing_else()
    {
        using var client = _console.CreateClient();

        var (_, asking) = await PostAsync(client, "Prune", []);

        Assert.Contains("Type the project's name to confirm", asking, StringComparison.Ordinal);
        Assert.Equal(0, _console.Broker.Pruned);

        var (_, wrong) = await PostAsync(client, "Prune", [("typed", "beta")]);

        Assert.Contains("not this project's name", Status(wrong), StringComparison.Ordinal);
        Assert.Equal(0, _console.Broker.Pruned);

        var (_, pruned) = await PostAsync(client, "Prune", [("typed", FilesConsole.Project)]);

        Assert.Equal(1, _console.Broker.Pruned);
        Assert.Contains("Removed 2 file(s), freeing 15 B.", Status(pruned), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_file_launches_it_on_this_machine_through_the_launcher()
    {
        var file = Path.Combine(Path.GetTempPath(), "parley-files-page-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(file, "net of tax", Ct);

        try
        {
            _console.Broker.Where = _ => new Wire.FileLocationReply { Ok = true, Machine = MachineName.Current, Path = file };

            using var client = _console.CreateClient();

            var (status, _) = await PostAsync(client, "Open&file=notes.md&inConversation=pricing", []);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal((file, false), Assert.Single(_console.Launched));

            var asked = Assert.Single(_console.Broker.Described);
            Assert.Equal("pricing", asked.ConversationId);
            Assert.Equal("notes.md", asked.Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Opening_a_file_on_another_machine_opens_a_checked_copy_read_through_the_broker()
    {
        // The console at the end of a tunnel: the broker's disk is not this one, so it reads the file and opens a copy,
        // as the terminal does, rather than naming a path on a machine the person is not at (SPEC.md §10.2).
        var bytes = "net of tax"u8.ToArray();

        _console.Broker.Files.Add(View("pricing", "brief.md", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "billing@alpha"));
        _console.Broker.Contents["brief.md"] = bytes;
        _console.Broker.Where = _ => new Wire.FileLocationReply { Ok = true, Machine = "broker-host", Path = "/srv/parley/brief.md" };

        using var client = _console.CreateClient();

        var (status, body) = await PostAsync(client, "Open&file=brief.md&inConversation=pricing", []);

        Assert.Equal(HttpStatusCode.OK, status);

        var (path, folder) = Assert.Single(_console.Launched);
        Assert.False(folder);
        Assert.StartsWith(_console.Copies, path, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal("pricing", Assert.Single(_console.Broker.Read).ConversationId);
        Assert.Contains("Opened a copy of brief.md", Status(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_that_do_not_hash_to_the_placed_file_are_never_copied_or_opened()
    {
        // notes.md was placed with another hash: whatever the broker streams under its name is not that file.
        // Exactly the placed size, so only the hash can tell: a longer stream is refused for its length first.
        _console.Broker.Contents["notes.md"] = "not placed"u8.ToArray();
        _console.Broker.Where = _ => new Wire.FileLocationReply { Ok = true, Machine = "broker-host", Path = "/srv/parley/notes.md" };

        using var client = _console.CreateClient();

        var (_, body) = await PostAsync(client, "Open&file=notes.md&inConversation=pricing", []);

        Assert.Empty(_console.Launched);
        Assert.False(Directory.Exists(_console.Copies) && Directory.EnumerateFiles(_console.Copies, "*", SearchOption.AllDirectories).Any());
        Assert.Contains("changed", Status(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_the_folder_launches_the_folder_of_the_conversation_being_viewed()
    {
        var folder = Directory.CreateTempSubdirectory("parley-files-page-").FullName;

        try
        {
            _console.Broker.Where = _ => new Wire.FileLocationReply { Ok = true, Machine = MachineName.Current, Path = folder };

            using var client = _console.CreateClient();

            await PostAsync(client, "Folder", [("conversation", "pricing")]);

            Assert.Equal((folder, true), Assert.Single(_console.Launched));

            var asked = Assert.Single(_console.Broker.Described);
            Assert.Equal("pricing", asked.ConversationId);
            Assert.Equal(string.Empty, asked.Name);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Theory]
    [InlineData("Remove")]
    [InlineData("Prune")]
    [InlineData("Open&file=notes.md&inConversation=pricing")]
    [InlineData("Folder")]
    public async Task A_post_without_this_pages_token_is_refused_and_nothing_is_asked_or_launched(string handler)
    {
        _console.Broker.Where = _ => new Wire.FileLocationReply { Ok = true, Machine = MachineName.Current, Path = Path.GetTempPath() };

        using var client = _console.CreateClient();

        var (status, _) = await PostAsync(
            client, handler, [("selected", "pricing/notes.md"), ("confirmed", "yes"), ("typed", FilesConsole.Project)], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_console.Broker.Removed);
        Assert.Equal(0, _console.Broker.Pruned);
        Assert.Empty(_console.Broker.Described);
        Assert.Empty(_console.Launched);
    }

    /// <summary>What the page says happened, decoded, or the page's text when it says nothing.</summary>
    /// <param name="body">The page.</param>
    /// <returns>The outcome line, as a person reads it.</returns>
    /// <remarks>
    /// Decoded because the outcome is rendered as text and Razor encodes it: an apostrophe arrives as an entity, and an
    /// assertion against the raw page would be checking the encoder rather than the words.
    /// </remarks>
    private static string Status(string body)
    {
        var status = Regex.Match(
            body, """<p role="status" class="outcome">(.*?)</p>""", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        return status.Success
            ? WebUtility.HtmlDecode(status.Groups[1].Value)
            : "(no outcome on the page) " + WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task The_upload_picker_offers_images_as_well_as_text()
    {
        // Without this the picker offered only .md and .txt after images could be placed (SPEC.md §9.5), so a person could
        // not choose a picture to upload from the page (review, lens 3 round 41).
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        var picker = Regex.Match(
            body, """<input id="upload" name="upload" type="file" accept="(?<accept>[^"]*)" """, RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.True(picker.Success, "the upload picker is not on the page");
        Assert.Equal(
            [".gif", ".jpeg", ".jpg", ".md", ".png", ".txt", ".webp"],
            picker.Groups["accept"].Value.Split(',', StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_conversation_is_typed_with_every_conversation_offered_and_its_chip_leads_back_to_every_file()
    {
        // Feature 042: a drop-down of every conversation grew past use, so the conversation is typed with suggestions. Narrowed
        // to one conversation, the filter must still offer the others, and the chip that takes the filter off must lead to
        // this project's files, whatever the address was after a form posted back.
        _console.Broker.Conversations.Add("returns");
        using var client = _console.CreateClient();

        var page = await client.GetStringAsync(new Uri(Page + "&conversation=pricing", UriKind.Relative), Ct);

        Assert.Matches("<input id=\"placeIn\" name=\"placeIn\" list=\"placeable\"", page);
        var offered = page[page.IndexOf("<datalist id=\"conversations\">", StringComparison.Ordinal)..page.IndexOf("</datalist>", StringComparison.Ordinal)];
        Assert.Contains("<option value=\"pricing\"", offered, StringComparison.Ordinal);
        Assert.Contains("<option value=\"returns\"", offered, StringComparison.Ordinal);
        Assert.Contains($"href=\"/Projects/Files?project={FilesConsole.Project}\" title=\"Take this filter off\">Conversation: pricing", page, StringComparison.Ordinal);
    }

    private static Wire.SharedFileView View(string conversation, string name, long size, string hash, string placedBy) => new()
    {
        ConversationId = conversation,
        Folder = conversation,
        Name = name,
        SizeBytes = size,
        Sha256 = hash,
        PlacedBy = placedBy,
        PlacedUtc = "2026-09-14T09:00:00Z"
    };

    /// <summary>Posts to one of the page's handlers, with the token a real form on the page would carry.</summary>
    /// <param name="client">A client that keeps the page's cookies.</param>
    /// <param name="handler">The handler, and any query it takes.</param>
    /// <param name="fields">The form's fields; the project is added.</param>
    /// <param name="withToken">Whether to carry the page's antiforgery token.</param>
    /// <returns>The status and the page returned.</returns>
    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        HttpClient client, string handler, (string Name, string Value)[] fields, bool withToken = true)
    {
        var form = new List<KeyValuePair<string, string>> { new("project", FilesConsole.Project) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

        if (withToken)
        {
            var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

            var token = Regex.Match(
                page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

            Assert.NotEmpty(token);

            form.Add(new("__RequestVerificationToken", token));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(new Uri("/Projects/Files?handler=" + handler, UriKind.Relative), content, Ct);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}
