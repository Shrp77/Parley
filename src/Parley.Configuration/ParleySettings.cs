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

    private const string WebUi = "webUi";

    private const string Hosts = "hosts";

    /// <summary>The most host names the web console takes from the file.</summary>
    public const int MostWebUiHosts = 16;

    /// <summary>
    /// The host names, beyond loopback and the address it binds, the web console answers to: <c>webUi.hosts</c>.
    /// </summary>
    /// <param name="root">The deployment root.</param>
    /// <param name="refused">Each entry left out, and why, in words for the person.</param>
    /// <returns>The names, lower case, each once; empty when nothing valid says.</returns>
    /// <remarks>
    /// <para>
    /// <b>For a name another program forwards to this machine's loopback</b>, such as a Tailscale <c>serve</c> address
    /// (owner, 2026-11-22). The console still binds loopback; the forwarder is the tunnel. What the list changes is only
    /// which <c>Host</c> the console accepts, and that check is the DNS rebinding defence, so every entry is a whole name
    /// written out: no wildcard, no IP address, nothing but letters, digits, hyphens and dots. A wildcard would let any
    /// name an attacker points at 127.0.0.1 through, which is the hole the check closes.
    /// </para>
    /// <para>
    /// Whoever can reach a listed name acts as the person: the console has no sign-in. Who can is the forwarder's to
    /// decide (a tailnet's access rules), which is why nothing adds a name but the person editing this file.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ReadWebUiHosts(string root, out IReadOnlyList<string> refused)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var names = new List<string>();
        var left = new List<string>();
        refused = left;

        var path = Path.Combine(root, FileName);

        try
        {
            if (LocalOpen.ReadText(path, json: true).Text is not { } text
                || StrictJson.Parse(text) is not JsonObject document
                || document[WebUi] is not JsonObject webUi
                || webUi[Hosts] is not { } listed)
            {
                return names;
            }

            if (listed is not JsonArray entries)
            {
                left.Add($"\"{WebUi}.{Hosts}\" in {path} is not a list of names, so none was taken.");

                return names;
            }

            foreach (var entry in entries)
            {
                var name = entry is JsonValue value && value.GetValueKind() == JsonValueKind.String
                    ? value.GetValue<string>().Trim().ToLowerInvariant()
                    : null;

                if (name is null || !IsHostName(name))
                {
                    left.Add($"{entry?.ToJsonString() ?? "null"} in \"{WebUi}.{Hosts}\" is not a whole host name (letters, digits, hyphens and dots; no wildcard or address), so it was left out.");
                }
                else if (names.Count >= MostWebUiHosts)
                {
                    left.Add($"\"{name}\" was left out: \"{WebUi}.{Hosts}\" takes at most {MostWebUiHosts} names.");
                }
                else if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            left.Add($"{path} could not be read, so the web console answers only to this machine's own names: {unreadable.Message}");

            return [];
        }
    }

    /// <summary>Whether a name is a whole DNS host name: dot-separated labels of letters, digits and inner hyphens.</summary>
    /// <param name="name">The name, already lower case.</param>
    /// <returns>True for a name with at least one letter in its last label, so never an IP address or a wildcard.</returns>
    internal static bool IsHostName(string name)
    {
        if (name.Length is 0 or > 253)
        {
            return false;
        }

        var labels = name.Split('.');

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || !label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        // An all-digit last label is an IPv4 address; a name always ends in a label with a letter.
        return labels[^1].Any(c => c is >= 'a' and <= 'z');
    }

    /// <summary>How many days the Inbox's Closed view covers.</summary>
    /// <param name="root">The deployment root.</param>
    /// <returns>The window, or <see cref="DefaultClosedDays"/> when nothing valid says.</returns>
    public static int ReadClosedDays(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var path = Path.Combine(root, FileName);

        try
        {
            // Through LocalOpen, as every file in the root is: a settings file somebody made a link to a share must not
            // make this lookup dial the host.
            return LocalOpen.ReadText(path, json: true).Text is { } text
                && StrictJson.Parse(text) is JsonObject document
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

            var (status, text) = LocalOpen.ReadText(path, json: true);

            if (status == LocalOpenStatus.OffThisMachine)
            {
                return $"{path} leads off this machine, so it was left as it is.";
            }

            if (text is not null)
            {
                if (StrictJson.Parse(text) is not JsonObject read)
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
