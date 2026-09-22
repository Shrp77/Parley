using System.Globalization;

namespace Parley.Core;

/// <summary>
/// Which agent this is, as distinct from what any project calls it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An agent is an identity; a name is a project's label for it.</b> One checkout enrolled in two projects
/// answers to a different name in each, and before this existed the thing that could be started was keyed by
/// that name — so the same checkout was startable in the project whose catalogue named it and unstartable in
/// the other. The defect was not what the catalogue held. It was that the arrangement could express the
/// state at all.
/// </para>
/// <para>
/// <b>It is opaque, and that is a requirement rather than tidiness.</b> Nothing parses it, orders by it or
/// reads meaning out of it. Anything derived from a path, a name, a project or a machine is a value that
/// changes when that thing changes, and the whole point of this is to survive a move and a rename.
/// </para>
/// <para>
/// <b>It never travels in a call, so Principle III is untouched.</b> An agent's name still comes from the
/// environment its server was started with and registration is still the adapter's own act on attach
/// (SPEC.md §12.3). This answers "what does this machine start", which is a question about a launch. If a
/// node ever started the wrong checkout for a requested name, the adapter that attaches registers what its
/// own environment says, so the request stays unsatisfied — a visible failure, never a forged identity.
/// </para>
/// <para>
/// <b>It is repository-scoped, not machine-scoped.</b> It lives in the repository, which is frequently
/// committed, so the same checkout on two machines carries the same identity — and that is right: what
/// distinguishes the running agents is the machine half of their names, as it always has. The case it
/// forbids is one machine cataloguing two checkouts of one repository, which is the copied-repository case
/// the enrolment help already calls the one thing Parley makes unforgeable.
/// </para>
/// </remarks>
public readonly record struct AgentIdentity
{
    private readonly Guid _value;

    private AgentIdentity(Guid value) => _value = value;

    /// <summary>Mints one that has never existed.</summary>
    /// <returns>The identity.</returns>
    /// <remarks>
    /// <b>Allocated rather than derived, and needing no authority to allocate it.</b> Enrolment happens on a
    /// machine with no guarantee of reaching anything else, so an identity that had to be handed out by a
    /// broker would make enrolling a thing that can fail for a reason nobody can act on.
    /// </remarks>
    public static AgentIdentity Mint() => new(Guid.NewGuid());

    /// <summary>Reads one that was written down.</summary>
    /// <param name="candidate">What was written.</param>
    /// <param name="identity">The identity, when this returns true.</param>
    /// <returns><see langword="true"/> when it is one.</returns>
    /// <remarks>
    /// Exact rather than lenient: a value this cannot read is a file somebody edited by hand or a version
    /// that wrote something else, and guessing at it would attach a repository to the wrong catalogue entry.
    /// </remarks>
    public static bool TryRead(string? candidate, out AgentIdentity identity)
    {
        if (Guid.TryParseExact(candidate?.Trim(), "D", out var parsed) && parsed != Guid.Empty)
        {
            identity = new AgentIdentity(parsed);

            return true;
        }

        identity = default;

        return false;
    }

    /// <summary>Reads one, or refuses.</summary>
    /// <param name="candidate">What was written.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="FormatException"><paramref name="candidate"/> is not one.</exception>
    public static AgentIdentity Read(string? candidate) =>
        TryRead(candidate, out var identity)
            ? identity
            : throw new FormatException(
                $"'{candidate}' is not an agent identity. One is written as {Example}.");

    /// <summary>What one looks like, for a refusal to show rather than describe.</summary>
    internal const string Example = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

    /// <summary>Whether this is an identity at all, rather than the default.</summary>
    public bool Known => _value != Guid.Empty;

    /// <summary>How it is written down, everywhere it is written down.</summary>
    /// <returns>The identity, lowercase and hyphenated.</returns>
    /// <remarks>
    /// One spelling, because it is a key: a catalogue written with braces and read without them is a
    /// catalogue whose entries cannot be found, and the failure looks like an agent that was never enrolled.
    /// </remarks>
    public override string ToString() => _value.ToString("D", CultureInfo.InvariantCulture);
}
