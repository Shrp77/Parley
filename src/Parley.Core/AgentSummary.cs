namespace Parley.Core;

/// <summary>
/// What one agent may learn about another, as returned by SPEC.md §10.1's <c>agents()</c>.
/// </summary>
/// <param name="Name">The registered name, which is what a question is addressed to.</param>
/// <param name="Adapter">
/// The named adapter attaching it to the broker. §10.1 lists this explicitly.
/// </param>
/// <param name="Tier">
/// Its capability tier (§11.1). Worth knowing: a question to a Headless agent triggers an
/// invocation and comes back in one shot, while one to a Pull-only agent waits until it drains.
/// </param>
/// <param name="Presence">
/// How reachable it is now (§14). Shown to peers deliberately, because an asker that can see
/// <see cref="AgentPresence.Stranded"/> knows an answer will be slow for a reason that has
/// nothing to do with the question — where inferring the same thing from silence takes a full
/// delivery window and teaches it nothing.
///
/// It does not tell an asker whether to ask. Nothing here gates routing except retirement, and
/// a retired agent is not listed at all.
/// </param>
/// <param name="Owns">
/// One line saying what this agent owns, or <see langword="null"/> if its launch did not say.
/// See <see cref="OwnershipStatement"/>: it is what turns a list of names into something an
/// asker can route by, and it is here rather than withheld because an operator wrote it for
/// exactly this purpose. Absent means unknown, which is true — the asker falls back to the name
/// and to referral, as it did before the field existed.
/// </param>
/// <remarks>
/// <para>
/// A reduced view of <c>AgentRecord</c>, and the omissions are the point. The stored record also
/// carries a workspace path, a session handle and the adapter's own configuration blob, and none
/// of those may cross to a peer: the workspace is a filesystem path on somebody else's machine,
/// the session handle is an internal identifier, and §12.1 makes the configuration free-form
/// precisely so it can hold endpoints and timeouts the broker never interprets. Handing an agent
/// a record it could read those from would breach rule 3 — never describe your internals — using
/// the one operation whose whole job is to describe agents.
/// </para>
/// <para>
/// This is a projection rather than a filter applied at the call site, because a filter is a
/// thing to forget. A new column on the stored record does not appear here until somebody adds
/// it, which is the moment to ask whether a peer should see it.
/// </para>
/// <para>
/// Retired agents are absent entirely rather than listed with a status. §14: a retired agent is
/// "not routed to, not offered in <c>agents()</c>". Listing one would invite a question that
/// admission then refuses, which teaches the asker nothing and costs it a turn.
/// </para>
/// </remarks>
public sealed record AgentSummary(
    AgentName Name,
    string Adapter,
    AgentTier Tier,
    AgentPresence Presence,
    string? Owns);
