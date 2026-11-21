using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Files;
using Parley.Core;

namespace Parley.Web.Pages.Conversations;

/// <summary>
/// A conversation's Media: its images as a grid and its other files as a list, newest first; and the one place the
/// console hands a placed image to a browser (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Serving another participant's bytes to a browser is a recorded reduction in the control</b> (§9.5), and this is
/// what answers it. Only a name ending in an image extension is served, and only from the conversation asked for. The
/// leading bytes must be the format the name says, or nothing is served, so a file named <c>.png</c> that holds a page or
/// a script never reaches a browser whatever its sniffing rules. The response names its type from the extension, turns
/// sniffing off, and carries a policy under which nothing in it can run, loaded in a page or opened on its own. An image
/// larger than <see cref="Placements.InlineLimitBytes"/> is served only when asked for by name.
/// </para>
/// <para>
/// <b>Reading records nothing.</b> The bytes come through the broker's <c>read_shared_file</c>, which records no fetch,
/// so viewing an image is not an act on the deployment, and a GET may serve it. It works wherever the console runs,
/// because the broker sends the bytes rather than the console reading a disk.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class MediaModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The policy an image is served under: nothing in it loads, runs or frames anything.</summary>
    internal const string ImagePolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";

    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which conversation.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The conversation's images, newest first.</summary>
    public IReadOnlyList<SharedFile> Images { get; private set; } = [];

    /// <summary>Its other files, newest first.</summary>
    public IReadOnlyList<SharedFile> Others { get; private set; } = [];

    /// <summary>Why the files could not be read, or <see langword="null"/>.</summary>
    public string? Problem { get; private set; }

    /// <summary>Lists the conversation's files.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        var read = await BrokerDirectory.ReadAsync(
            () => Placements.EveryAsync(new BrokerClient(directory.ClientFor(project)), Id, HttpContext.RequestAborted))
            .ConfigureAwait(false);

        (Images, Others) = Placements.Media(read.Value ?? []);
        Problem = read.Problem;

        return Page();
    }

    /// <summary>Serves one placed image's bytes, or refuses.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="sha">The hash the page that linked it saw.</param>
    /// <param name="large">Whether the person asked for an image larger than is shown inline.</param>
    /// <returns>The image, or 404, 413 or 415.</returns>
    /// <remarks>
    /// 404 for a name that is not an image's, a file that is not there and a hash that is no longer its own, so a link
    /// from a page drawn before a file changed shows nothing rather than other bytes. 413 for an image over the inline
    /// limit not asked for by name; 415 for bytes that are not the format the name says.
    /// </remarks>
    public async Task<IActionResult> OnGetImageAsync(
        [FromQuery] string? name, [FromQuery] string? sha, [FromQuery] bool large = false)
    {
        if (directory.Find(Project) is not { } project
            || string.IsNullOrWhiteSpace(Id)
            || string.IsNullOrEmpty(name)
            || ImageSignature.ContentType(name) is not { } type
            || !ContentHash.TryParse(sha, out var expected))
        {
            return NotFound();
        }

        var limit = large ? Placements.CopyLimitBytes : Placements.InlineLimitBytes;

        var read = await BrokerDirectory.ReadAsync(
            () => new BrokerClient(directory.ClientFor(project))
                .ReadSharedFileAsync(Id, name, expected, limit, HttpContext.RequestAborted))
            .ConfigureAwait(false);

        if (read.Value is not { } got || got.Refusal is not null || got.File is null)
        {
            return NotFound();
        }

        if (got.TooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var bytes = got.Bytes!.Value;

        if (!ImageSignature.Matches(name, bytes.Span))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var headers = Response.Headers;
        headers.ContentSecurityPolicy = ImagePolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentDisposition = "inline";

        // The address names the hash, and a placed file never changes, so what was served under it is what it will
        // always serve. Private: it is the person's, never a shared cache's.
        headers.CacheControl = "private, max-age=31536000, immutable";

        return File(bytes.ToArray(), type);
    }

    /// <summary>The address of one image's bytes.</summary>
    /// <param name="url">The page's URL helper.</param>
    /// <param name="project">The project.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="file">The image.</param>
    /// <param name="large">Whether to ask for it past the inline limit.</param>
    /// <returns>The address, built by the helper so no value can add a parameter of its own.</returns>
    public static string? ImageUrl(IUrlHelper url, string? project, string conversation, SharedFile file, bool large = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(file);

        return url.Page(
            "/Conversations/Media",
            "Image",
            large
                ? new { project, id = conversation, name = file.Name.Value, sha = file.Sha256.Value, large = true }
                : new { project, id = conversation, name = file.Name.Value, sha = file.Sha256.Value });
    }

    /// <summary>The anchor a placement has in its conversation's transcript.</summary>
    /// <param name="file">The file.</param>
    /// <returns>An identifier made of the name's key, which holds only letters, digits, '.', '_' and '-'.</returns>
    public static string Anchor(SharedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return "f-" + file.Name.Key;
    }
}
