
namespace Parley.Terminal.Tests;

/// <summary>
/// Driving the composer, which now asks who before it asks what.
/// </summary>
/// <remarks>
/// <para>
/// **The peer is chosen from the roster rather than typed**, so every test that composes has two
/// steps where it had one. Written once here rather than seven times: the sequence is the surface's,
/// not any one test's, and seven copies of it would be seven things to edit the next time the
/// composer grows a field.
/// </para>
/// <para>
/// The picker is a <c>ChoiceDialog</c> and carries the roster's own spelling of a name — a
/// registration is <c>role@machine</c>, a catalogue key is a role — which is exactly why choosing
/// beats typing: the label is whatever the list already says, and the identity comes back with it.
/// </para>
/// </remarks>
internal static class Composing
{
    /// <summary>Opens the composer and chooses a peer, leaving the form on screen.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="open">Whatever opens the composer — asking or telling, from either region.</param>
    /// <param name="title">The composer's title, which the picker shares.</param>
    /// <param name="peer">The peer's row, as the roster spells it.</param>
    /// <param name="settled">Awaited after the choice, so the form is up before the caller fills it.</param>
    /// <returns>The form.</returns>
    internal static async Task<FormDialog> WithAsync(
        Driving.Session driving,
        Func<Task> open,
        string title,
        string peer,
        Func<Task> settled)
    {
        await open().ConfigureAwait(false);

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);

        driving.Row(picking.Choices, peer);
        driving.Enter();

        await settled().ConfigureAwait(false);

        return (FormDialog)driving.Find(title);
    }

    /// <summary>Starts a conversation the way a person does now: choose who, name it, then say it in the box.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="shell">The surface.</param>
    /// <param name="peer">The peer's row, as the roster spells it.</param>
    /// <param name="name">The conversation's name.</param>
    /// <param name="words">What is typed into the box and sent.</param>
    /// <remarks>
    /// Starting a conversation asks for its name and nothing else (the owner's call of 2026-10-05); what is said is
    /// written in the one input line every conversation has, so the helper ends by pressing Enter there.
    /// </remarks>
    internal static async Task StartAndSayAsync(
        Driving.Session driving, Shell shell, string peer, string name, string words)
    {
        await shell.Start().ConfigureAwait(false);

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);

        driving.Row(picking.Choices, peer);
        driving.Enter();

        await shell.Settled.ConfigureAwait(false);

        var naming = (FormDialog)driving.Find(Participation.NameTitle);

        naming.Fill(0, name);
        naming.Finish(taken: true);

        await shell.Settled.ConfigureAwait(false);

        shell.Line.Written = words;
        shell.Line.Accept();

        await shell.Settled.ConfigureAwait(false);
    }
}
