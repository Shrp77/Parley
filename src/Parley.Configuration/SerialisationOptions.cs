using System.Text.Json;

namespace Parley.Configuration;

/// <summary>
/// The one set of JSON options every configuration file Parley writes is rendered with.
/// </summary>
/// <remarks>
/// Indented, because every file it touches is one a person edits by hand afterwards and a
/// single-line <c>.mcp.json</c> is a file somebody will not correct. Relaxed escaping, because
/// the default encoder turns the <c>${PARLEY_WORKSPACE:-...}</c> form and any non-ASCII
/// character in an <c>owns</c> line into escapes that are valid JSON and unreadable prose.
/// </remarks>
public static class SerialisationOptions
{
    /// <summary>Indented output with escaping kept to what JSON actually requires.</summary>
    public static JsonSerializerOptions Indented { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
    };
}
