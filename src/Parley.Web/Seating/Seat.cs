using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>
/// A person who has joined this deployment, and the agent identity they speak through.
/// </summary>
/// <param name="Person">What they typed when they joined, lowercased and validated as a role.</param>
/// <remarks>
/// <para>
/// SPEC.md §11.2 admits a human as a responder and §11.3 gives them an identity —
/// <c>human:&lt;name&gt;</c> — so a person taking part was always within the protocol. What was
/// missing was a seat: somewhere for that identity to be held across turns, which is the one
/// thing §11.2 says a responder needs and a command-line invocation cannot give a person who is
/// sitting at a browser.
/// </para>
/// <para>
/// The name is a role and nothing more, because the machine half is not the person's to choose.
/// A seat is taken on the machine the console is running on, so the console supplies that half —
/// which is also what stops a person seating themselves on a machine they are not at, and
/// answering as somebody who is.
/// </para>
/// </remarks>
public sealed record Seat(string Person)
{
    /// <summary>What an agent registered by this console reports as its adapter.</summary>
    /// <remarks>
    /// Load-bearing rather than decorative. It is how a registry row is known to be a person's
    /// seat rather than a process's registration, and <see cref="SeatBook"/> refuses to take a
    /// name whose row says anything else — so a person typing <c>orders</c> is told the name is
    /// taken instead of quietly speaking as the orders agent.
    /// </remarks>
    public const string Adapter = "parley-console";

    /// <summary>The identity SPEC.md §11.3 records against everything they say.</summary>
    public string Responder =>
        string.Create(CultureInfo.InvariantCulture, $"human:{Person}");

    /// <summary>The agent name this seat holds on the console's own machine.</summary>
    public AgentName Name => AgentName.Create(
        string.Create(CultureInfo.InvariantCulture, $"{Person}@{MachineName.Current}"));

    /// <summary>Reads a name somebody typed, as a seat or as a reason it cannot be one.</summary>
    /// <param name="typed">What they wrote in the join form.</param>
    /// <param name="seat">The seat, when this returns <see langword="true"/>.</param>
    /// <param name="problem">Why not, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> if the text can name a person here.</returns>
    /// <remarks>
    /// Lowercased before it is judged rather than after being refused, because "Arvind" is not a
    /// mistake a person should have to be told about — the constraint is the protocol's spelling
    /// of a role, not a rule about how anybody writes their own name. Everything else is refused
    /// with the rule stated, since a join form that says only "invalid" leaves somebody guessing
    /// at a regular expression.
    /// </remarks>
    public static bool TryRead(
        string? typed,
        [NotNullWhen(true)] out Seat? seat,
        [NotNullWhen(false)] out string? problem)
    {
        seat = null;
        problem = null;

        var trimmed = typed?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            problem = "Type a name to join as.";
            return false;
        }

        var lowered = trimmed.ToLowerInvariant();

        if (!AgentName.IsRole(lowered))
        {
            problem =
                $"'{trimmed}' cannot be a name here. It has to start with a letter and hold "
                + "only letters, digits and hyphens, up to 32 characters — the same shape every "
                + "other agent's name has, because that is what this one becomes.";

            return false;
        }

        seat = new Seat(lowered);

        return true;
    }
}
