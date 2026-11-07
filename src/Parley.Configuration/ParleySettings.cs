using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parley.Configuration;

/// <summary>
/// Settings for Parley as a whole on this machine, read by every surface: <c>&lt;root&gt;/settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file beside the projects, not the browser's storage</b> (spec 036, research R8). The web console keeps its theme and
/// density in the browser because they are about one browser. The Inbox's closed window is read by the terminal too, so it
/// lives where both find it: the deployment root each already knows.
/// </para>
/// <para>
/// <b>Reading never fails.</b> A missing file, a missing key, the wrong type or a value out of range reads as the default, so
/// a hand-edited file cannot stop a surface opening. Writing is atomic and keeps every key it does not know, the byte-order
/// mark and the line endings, as a project's own file is written.
/// </para>
/// </remarks>
public static class ParleySettings
{
    /// <summary>The file's name in the deployment root.</summary>
    public const string FileName = "settings.json";

    /// <summary>The closed window when nothing says otherwise.</summary>
    public const int DefaultClosedDays = 7;

    /// <summary>The shortest window.</summary>
    public const int FewestClosedDays = 1;

    /// <summary>The longest window.</summary>
    public const int MostClosedDays = 365;

    private const string Inbox = "inbox";

    private const string ClosedDays = "closedDays";

    /// <summary>How many days the Inbox's Closed view covers.</summary>
    /// <param name="root">The deployment root.</param>
    /// <returns>The window, or <see cref="DefaultClosedDays"/> when nothing valid says.</returns>
    public static int ReadClosedDays(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var path = Path.Combine(root, FileName);

        try
        {
            if (!File.Exists(path))
            {
                return DefaultClosedDays;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return StrictJson.Parse(StrictJson.Read(stream)) is JsonObject document
                && document[Inbox] is JsonObject inbox
                && inbox[ClosedDays] is JsonValue value
                && value.GetValueKind() == JsonValueKind.Number
                && value.TryGetValue<int>(out var days)
                && days is >= FewestClosedDays and <= MostClosedDays
                    ? days
                    : DefaultClosedDays;
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return DefaultClosedDays;
        }
    }

    /// <summary>Sets how many days the Inbox's Closed view covers.</summary>
    /// <param name="root">The deployment root.</param>
    /// <param name="days">The window.</param>
    /// <returns><see langword="null"/> when it was written, or why it was not, in words for the person.</returns>
    /// <remarks>
    /// A file that cannot be parsed is not written over: that would discard whatever else somebody put in it.
    /// </remarks>
    public static string? WriteClosedDays(string root, int days)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        if (days is < FewestClosedDays or > MostClosedDays)
        {
            return $"The window is a whole number of days from {FewestClosedDays} to {MostClosedDays}.";
        }

        var path = Path.Combine(root, FileName);

        try
        {
            JsonObject document;

            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (StrictJson.Parse(StrictJson.Read(stream)) is not JsonObject read)
                {
                    return $"{path} does not hold a JSON object, so it was left as it is.";
                }

                document = read;
            }
            else
            {
                Directory.CreateDirectory(root);
                document = [];
            }

            if (document[Inbox] is not JsonObject inbox)
            {
                if (document.ContainsKey(Inbox))
                {
                    return $"{path} has an \"{Inbox}\" that is not an object, so it was left as it is.";
                }

                inbox = [];
                document[Inbox] = inbox;
            }

            inbox[ClosedDays] = days;

            ProjectFolder.Commit(path, document);

            return null;
        }
        catch (JsonException unparsed)
        {
            return $"{path} could not be read as JSON, so it was left as it is: {unparsed.Message}";
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            return $"{path} could not be written: {failed.Message}";
        }
    }
}
