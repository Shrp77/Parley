using System.Globalization;

namespace Parley.Client.Rendering;

/// <summary>
/// One way to write an instant for somebody reading a screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The screen is local; the export is not.</b> A transcript on screen is read by the person who
/// opened it, on the machine they are sitting at, so UTC makes them do arithmetic to answer "was
/// that this morning". A file <c>parley export</c> wrote is read by somebody else, somewhere else,
/// possibly months later — and a file saying <c>14:03</c> without saying whose <c>14:03</c> is
/// worse than one saying <c>14:03Z</c>.
/// </para>
/// <para>
/// So the conversion belongs here, at the views, and never inside
/// <see cref="Transcript.Write"/>. A test asserts the export contains no local time, because the
/// obvious refactor — "make the transcript writer format times nicely" — is the one that breaks it.
/// </para>
/// <para>
/// <b>The stored value never changes.</b> Everything in the store is UTC and stays UTC; this is a
/// rendering and nothing reads it back.
/// </para>
/// </remarks>
public static class LocalTime
{
    /// <summary>Writes an instant in the reader's own time zone.</summary>
    /// <param name="instant">The instant, however it was stored.</param>
    /// <param name="zone">The zone to render in, or null for the machine's own.</param>
    /// <returns>The instant, in local time, to the second.</returns>
    /// <remarks>
    /// <para>
    /// The zone is a parameter so a test can assert a conversion happened rather than asserting
    /// that two values agree on the machine it happens to run on. A suite that only ever runs in
    /// UTC cannot tell a working conversion from no conversion at all, which is the failure this
    /// signature exists to make impossible.
    /// </para>
    /// <para>
    /// Invariant culture, deliberately. The build makes culture-sensitive formatting an error, and
    /// a timestamp whose separator depends on the operator's regional settings is a timestamp two
    /// people reading the same deployment describe differently.
    /// </para>
    /// </remarks>
    public static string Write(DateTimeOffset instant, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Local);

        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes just the time of day, for a column that already has the date.</summary>
    /// <param name="instant">The instant.</param>
    /// <param name="zone">The zone to render in, or null for the machine's own.</param>
    /// <returns>The time of day, in local time.</returns>
    public static string Clock(DateTimeOffset instant, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Local);

        return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>What the surface says once, so a reader knows what the times are.</summary>
    /// <param name="zone">The zone, or null for the machine's own.</param>
    /// <returns>The note.</returns>
    /// <remarks>
    /// Said once per view rather than per row. A screen full of timestamps with no zone on any of
    /// them is ambiguous in exactly the way the export is careful not to be, and repeating an
    /// offset on every line would spend the width a message body needs.
    /// </remarks>
    public static string Note(TimeZoneInfo? zone = null) =>
        $"Times are local ({(zone ?? TimeZoneInfo.Local).StandardName}).";
}
