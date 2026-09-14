using System.Globalization;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Top;

/// <summary>
/// Giving one agent an instruction from the terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This surface used to read instructions and give none</b>, on the argument that the console had a
/// composer for it. The owner's parity decision reversed that: a person at a terminal over SSH is the one who
/// cannot open the console, and a surface that shows what an agent was told but cannot tell it anything sends
/// them elsewhere for the half that matters.
/// </para>
/// <para>
/// <b>Not behind a typed name.</b> It deletes nothing and ends nobody's work; it puts a person's words into an
/// agent's turn, recorded as theirs, and the agent may decline (§11.5). So the door is a description and the
/// words themselves, and what the agent says back is read under <see cref="Telling"/>.
/// </para>
/// </remarks>
internal static class Instructing
{
    /// <summary>What the action is called, before the agent's name.</summary>
    internal const string Title = "Instruct ";

    private const string NothingSent = "Nothing was sent.";

    /// <summary>Opens the form that instructs an agent.</summary>
    /// <param name="app">The application the form opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Who is instructed.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the form is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var described =
            $"{Safe.Text(agent.Value)} will be given your words as a turn. It may decline, which is not a failure. "
            + "The instruction is recorded as yours, and what it says back appears under what you have told it.";

        var form = new FormDialog(
            Title + Safe.Text(agent.Value) + Environment.NewLine + described,
            [new FormField("What should it do?", string.Empty, Problem, Long: true)]);

        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null || string.IsNullOrWhiteSpace(values[0]))
            {
                _ = done(NothingSent);

                return;
            }

            var text = values[0];

            queue(t => InstructAsync(client, agent, text, t, done));
        };

        return Task.CompletedTask;
    }

    private static string? Problem(string said) =>
        string.IsNullOrWhiteSpace(said)
            ? "Say what it should do."
            : said.Length > MessageLimits.MaxPayloadCharacters
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"That is {said.Length} characters and the limit is {MessageLimits.MaxPayloadCharacters}.")
                : null;

    /// <summary>Gives the instruction and says what became of it, in the console's words.</summary>
    private static async Task InstructAsync(
        BrokerClient client,
        AgentName agent,
        string text,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.InstructAsync(agent, text, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(outcome.Problem!).ConfigureAwait(false);

            return;
        }

        var result = outcome.Value!;
        var reply = result.Reply is { Length: > 0 } said ? " " + Safe.Text(said.ReplaceLineEndings(" ")) : string.Empty;

        // Declined is not phrased as a failure, and still running is the ordinary answer for real work (§10.2).
        await done(result.Outcome switch
        {
            InstructionOutcome.Taken => "Taken." + reply,
            InstructionOutcome.Declined => "The agent declined, which is its right." + reply,
            InstructionOutcome.Undeliverable =>
                "The machine that holds this agent is not connected, so nothing was delivered.",
            InstructionOutcome.Failed => "The turn did not complete." + reply,
            _ => "Given to the agent, and the turn is still running. Its answer will appear under what you have told it.",
        }).ConfigureAwait(false);
    }
}
