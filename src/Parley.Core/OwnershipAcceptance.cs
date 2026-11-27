namespace Parley.Core;

/// <summary>
/// What a person agreed an agent owns, and how they arrived at it (SPEC.md §17 question 10).
/// </summary>
/// <param name="Statement">The line agreed. Not what peers read — see <see cref="OwnershipRecord"/>.</param>
/// <param name="Source">Which of the three routes produced it.</param>
/// <param name="ProposedLength">
/// The length of the <b>agent's own</b> message, or <see langword="null"/> where there was no
/// proposal. Never a length a person wrote.
/// </param>
/// <param name="Conversation">
/// The discussion it was agreed in, or <see langword="null"/> only when
/// <see cref="OwnershipSource.Composed"/>. A plain identifier, as every other operation here takes
/// one — there is no conversation-name type, and §17 question 10 needs none.
/// </param>
/// <remarks>
/// <para>
/// <b>This is not the registry's <c>owns</c> and must never be confused with it.</b> What peers read
/// is filled from the launch environment when the agent attaches (§12.3), so recording an acceptance
/// changes nothing an asker sees until the agent next starts. The separation is the whole of what
/// lets a surface say a line is *pending* by comparing two facts rather than by keeping a flag
/// somebody has to remember to clear.
/// </para>
/// <para>
/// No operation accepts an <c>owns</c> line from the agent it describes, and this one does not
/// either: it records a person's decision, and the launch configuration remains the only source of
/// the line itself. A broker able to write that field would be a route around Principle III that no
/// adapter had to cooperate with.
/// </para>
/// </remarks>
public sealed record OwnershipAcceptance(
    string Statement,
    OwnershipSource Source,
    int? ProposedLength,
    string? Conversation)
{
    /// <summary>Why this cannot be recorded, or <see langword="null"/> when it can.</summary>
    /// <returns>The reason, in the words an operator reads.</returns>
    /// <remarks>
    /// <para>
    /// Asked of <see cref="OwnershipStatement"/> rather than restating its rule, so that what a
    /// surface refuses and what storage refuses cannot disagree. The rest is the shape rule the
    /// schema also holds: a composed line had no proposal and no discussion, and anything else had
    /// both.
    /// </para>
    /// <para>
    /// Returns rather than throws because every caller wants the message: a surface puts it in front
    /// of a person, and the broker sends it back as the reply's <c>problem</c>.
    /// </para>
    /// </remarks>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Statement))
        {
            return "an empty line is not a statement of ownership. Decline instead, and the " +
                   "existing line stays as it is.";
        }

        if (!OwnershipStatement.IsLaunchable(Statement, out var wrong))
        {
            return wrong;
        }

        if (Source == OwnershipSource.Composed)
        {
            return ProposedLength is not null
                ? "a composed line has no proposal, so there is no length an agent wrote."
                : Conversation is not null
                    ? "a composed line was not discussed, so there is no conversation to name."
                    : null;
        }

        if (Conversation is null)
        {
            return "a line the agent proposed was agreed somewhere, so the conversation must be named.";
        }

        return Source == OwnershipSource.Automatic && ProposedLength is null
            ? "a line accepted automatically is the agent's own answer, so the length it wrote is known."
            : null;
    }
}

/// <summary>
/// What was accepted for an agent, beside what its peers actually read.
/// </summary>
/// <param name="Recorded">Whether the acceptance was written.</param>
/// <param name="Problem">Why it was not, or <see langword="null"/> when it was.</param>
/// <param name="LiveStatement">
/// What peers read <em>now</em>, from the registry, or <see langword="null"/> when the agent has no
/// line at all.
/// </param>
/// <remarks>
/// <see cref="Pending"/> is derived rather than stored, which is the point: both halves are
/// observations, so the state cannot be wrong and nothing has to remember to clear it. A flag would
/// be a third fact about the same thing, and whoever noticed the restart would have to clear it.
/// </remarks>
public sealed record OwnershipRecord(bool Recorded, string? Problem, string? LiveStatement)
{
    /// <summary>Whether what was accepted is not yet what peers read.</summary>
    /// <remarks>
    /// True until the agent next starts, because the registry is filled from the launch environment
    /// on attach. A surface showing the line says so, so that nobody reads a roster believing it
    /// already carries what they agreed.
    /// </remarks>
    public bool Pending { get; init; }
}
