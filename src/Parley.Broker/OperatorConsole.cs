using Microsoft.Data.Sqlite;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// The operations of SPEC.md §10.2, available to the person running the broker and never to an
/// agent.
/// </summary>
/// <remarks>
/// <para>
/// A separate type because §10.2 opens by warning that an adapter surfacing any of these into an
/// agent's tool set would breach §10.1, and the protection the spec offers is a naming
/// convention — <c>force_conclude</c> is deliberately not called <c>conclude</c>, so that the
/// wrong one cannot be bound by accident. That is a real safeguard and a thin one. Keeping these
/// off <see cref="Broker"/> makes it structural instead: an adapter is handed a broker, and a
/// broker has no operator operations on it to bind.
/// </para>
/// <para>
/// It is a guard against the easy mistake, not a sandbox, and the difference is worth stating
/// so nobody mistakes it for isolation. An adapter references this assembly and gets
/// <c>Parley.Store</c> with it, so it can construct a repository and write whatever it likes —
/// as it could already, by saving an exchange directly. Nothing here is a privilege boundary;
/// the trust boundary is §13, at the message, and the process boundary is the deployment's.
/// What this removes is the specific failure §10.2 describes: the wrong method bound because it
/// was on the object already in hand and the signatures nearly matched.
/// </para>
/// <para>
/// The distinction these enforce is not a hierarchy among agents. No agent may conclude over
/// another's objection, and the person running the broker is not an agent — the same division as
/// <c>retire</c>. The broker never overrules a participant; the operator always may.
/// </para>
/// <para>
/// Deliberately not lifecycle-guarded, unlike every operation on <see cref="Broker"/>. Those
/// refuse before <see cref="Broker.StartAsync"/> because they decide against state that §14's
/// attach sequence exists to invalidate — this agent's debts, its liveness. These decide
/// against nothing: closing a conversation is an instruction, not an inference. Requiring a
/// started broker would make the escape hatch depend on the lifecycle an operator is most likely
/// reaching for it because of.
/// </para>
/// </remarks>
public sealed class OperatorConsole
{
    private readonly ConversationRepository _conversations;

    /// <summary>
    /// Creates a console over an open connection.
    /// </summary>
    /// <param name="connection">
    /// An open, already-migrated connection to the project's database. Borrowed, never closed;
    /// the caller owns its lifetime, as with <see cref="Broker"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public OperatorConsole(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _conversations = new ConversationRepository(connection);
    }

    /// <summary>
    /// Closes a conversation regardless of its participants' votes.
    /// </summary>
    /// <param name="conversationId">The discussion to end.</param>
    /// <param name="nowUtc">The instant of closure.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if this call closed it; <see langword="false"/> if it had already
    /// concluded, in which case the original instant is left alone.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// It exists because a decline blocks indefinitely (SPEC.md §5.0) and the agent holding it
    /// may simply be wrong. Without it the closure poll introduces a state nothing can resolve:
    /// <see cref="Broker.TickAsync"/> will not conclude over an objection, and a decline lapses
    /// only if its holder is retired or becomes unreachable, so the conversation would stay
    /// polling for as long as the database survived.
    /// </para>
    /// <para>
    /// Closes a conversation in any non-terminal state, not only one that is polling. An operator
    /// ending a discussion has no reason to first find an agent willing to propose closure, and
    /// requiring a poll to exist would make the escape hatch depend on the machinery it exists to
    /// escape.
    /// </para>
    /// </remarks>
    public async Task<bool> ForceConcludeAsync(
        string conversationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        return await _conversations.ForceConcludeAsync(
            conversationId, nowUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
