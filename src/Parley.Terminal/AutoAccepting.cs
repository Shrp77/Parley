using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// The project's Auto Accept switch (SPEC.md §10.2 <c>set_auto_accept</c>, FR-010).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read before it is offered, so the dialog says what it is now.</b> A switch that asked "turn it on?" without saying
/// whether it was already on would be a person deciding blind about the one setting that decides whether anybody reads
/// an agent's line before its peers do.
/// </para>
/// <para>
/// <b>Answers already waiting stay waiting</b> (FR-015), and the dialog says so: turning the switch on is a statement
/// about what happens next, not a decision about answers a person has not looked at.
/// </para>
/// </remarks>
internal static class AutoAccepting
{
    /// <summary>The door the switch passes through.</summary>
    internal const string Title = "Auto Accept";

    /// <summary>The choice that changes nothing, which the dialog opens on.</summary>
    internal const string Keep = "Leave it as it is";

    /// <summary>The choice that turns it on.</summary>
    internal const string TurnOn = "Turn it on";

    /// <summary>The choice that turns it off.</summary>
    internal const string TurnOff = "Turn it off";

    /// <summary>Reads the switch, and offers to change it.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="project">The project, for what the dialog names.</param>
    /// <param name="queue">Runs the read and the write, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the read has been queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        ProjectSettings project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async t =>
        {
            var read = await BrokerDirectory.ReadAsync(
                async () => await client.FacilitationAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done($"Auto Accept could not be read: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

                return;
            }

            var on = read.Value!.AutoAccept;
            var asking = Prompting.Choose(Title, Described(project.Name.Value, on), [Keep, on ? TurnOff : TurnOn]);
            var open = Prompting.Open(app, asking);

            asking.Done += (_, chosen) =>
            {
                Prompting.Close(app, open);

                if (chosen is not (TurnOn or TurnOff))
                {
                    _ = done($"Auto Accept is still {(on ? "on" : "off")}.");

                    return;
                }

                queue(async setting =>
                {
                    var set = await BrokerDirectory.ReadAsync(
                        async () => await client.SetAutoAcceptAsync(chosen == TurnOn, setting).ConfigureAwait(false))
                        .ConfigureAwait(false);

                    await done(set.Reachable
                            ? $"Auto Accept is {(set.Value ? "on" : "off")} for {Safe.Text(project.Name.Value)}."
                            : $"Auto Accept was not changed: {Safe.Text(set.Problem!)}")
                        .ConfigureAwait(false);
                });
            };
        });

        return Task.CompletedTask;
    }

    /// <summary>What the person reads before choosing.</summary>
    /// <param name="project">The project's name.</param>
    /// <param name="on">Whether the switch is on now.</param>
    /// <returns>The description.</returns>
    internal static string Described(string project, bool on) =>
        $"Auto Accept is {(on ? "ON" : "OFF")} for {Safe.Text(project)}.{Environment.NewLine}{Environment.NewLine}{Meaning}";

    /// <summary>What the setting decides, as the configuration view and the dialog both say it.</summary>
    /// <remarks>
    /// One text for both, so the view that explains the setting and the dialog that changes it cannot describe two
    /// different switches. It opens by saying what is being accepted, because the name alone does not.
    /// </remarks>
    internal static string Meaning { get; } =
        """
        Parley asks every agent in this project what it owns. This setting decides what happens to the answer.

        On: the answer becomes the agent's ownership line with no person reading it first, unless it cannot be
        one or the agent is on another machine. It is marked as accepted automatically, and you can correct it in
        the ownership review.

        Off: every answer waits in the ownership review for a person to accept, edit or discard.

        Answers already waiting stay waiting either way.
        """.ReplaceLineEndings(Environment.NewLine);

    /// <summary>The setting as the configuration view lists it.</summary>
    /// <param name="on">Whether it is on.</param>
    /// <returns>The row and its meaning.</returns>
    internal static Setting AsSetting(bool on) => new(Title, on ? "on" : "off", Meaning);
}
