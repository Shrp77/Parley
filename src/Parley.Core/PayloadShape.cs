using System.Text.Json;

namespace Parley.Core;

/// <summary>
/// Reads the parts of a Request or Result payload the protocol itself depends on (SPEC.md §5.1, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// The payload is otherwise opaque to the broker, which delivers it as the document it is (§13). One
/// fact about it is protocol rather than content: what outcome a Result gave the Request — because the
/// exchange's rolled-up outcome is derived from it rather than taken on the sender's word.
/// </para>
/// </remarks>
public static class PayloadShape
{
    /// <summary>Whether a payload could be read two ways by the store and by the broker.</summary>
    /// <param name="payloadJson">A Request, Clarify or Result payload.</param>
    /// <returns>
    /// <see langword="true"/> when the payload holds a NUL character, or text that cannot be decoded (a lone surrogate, in a
    /// name or in a value), or writes one member name twice in one object, or has a member name with a NUL in it; a document
    /// that is not JSON at all is <see langword="false"/>, left to the store's own check.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The store and the broker read a repeated name differently.</b> SQLite's <c>json_extract</c> and <c>json_each</c>
    /// take the first, and <see cref="JsonDocument"/> takes the last, so a Result writing <c>outcome</c> twice passed the
    /// schema's checks on one reading and closed the exchange on the other: answered in the store, blocked with no blocker in
    /// the broker's roll-up. A payload is data (§13) and is never given two meanings: it is refused whole.
    /// </para>
    /// <para>
    /// <b>A NUL is a repeat in disguise, in a name or after the document.</b> SQLite matches a path against a name as a C
    /// string, so <c>outcome\u0000</c> is the member <c>outcome</c> to it and a different name to <see cref="JsonDocument"/>;
    /// and it stops reading a text at a raw NUL, so <c>{"ask":"a","ask":" "}</c> followed by a NUL and more is a valid
    /// document to the store and an unreadable one to the broker, which then left the repeat unseen (review, security lens, runs
    /// 4 and 5, 0.63.0 and 0.63.2: 800,000 random payloads and every single-character variation of every member name the broker
    /// reads showed no other disagreement). No honest payload holds a NUL.
    /// </para>
    /// <para>
    /// <b>Text that cannot be decoded is refused.</b> A lone surrogate in a value, such as an emoji cut in half, is valid to the
    /// store and throws when the broker reads the string: the exchange closed Blocked and the Inbox, which could not read the
    /// words, showed it Done with its blocker lost. The sender is told to send it again whole.
    /// </para>
    /// <para>
    /// A document nested deeper than the parser allows (64 levels) is not read here and is left to the store; every reader of a
    /// payload in the broker and the clients uses the same limit and fails closed on it, which is why none can disagree.
    /// </para>
    /// </remarks>
    public static bool HasAmbiguousName(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        if (payloadJson.Contains('\0', StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var lenient = JsonDocument.Parse(payloadJson);

            if (CannotBeReadOneWay(lenient.RootElement))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // A lone surrogate in the string itself: it cannot be made into UTF-8, and no reader could read it.
            return true;
        }
        catch (InvalidOperationException)
        {
            // A name or a value that is a lone surrogate escape: it parsed, and reading it as text is what throws.
            return true;
        }

        try
        {
            using var strict = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { AllowDuplicateProperties = false });

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // Every name and every string value at every depth: the parse allows 64 levels, so the walk is bounded. Reading a string
    // is what throws for one that cannot be decoded.
    private static bool CannotBeReadOneWay(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (member.Name.Contains('\0', StringComparison.Ordinal) || CannotBeReadOneWay(member.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (CannotBeReadOneWay(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                _ = element.GetString();

                return false;
            default:
                return false;
        }
    }

    /// <summary>The words an agent is told when its payload could be read two ways.</summary>
    public const string AmbiguousNameRefusal =
        "A message is one JSON document that names each member once, holds no NUL character, and has text that can be decoded " +
        "(no half of an emoji): a repeated or NUL-ended name, or such text, is read one way by the store and another by the " +
        "broker, so the payload is refused whole. Send it again with each name once and the text whole (SPEC.md §13).";

    /// <summary>The one item a Result names: the Request, answered as a whole (SPEC.md §5.1).</summary>
    /// <remarks>A Request declares no identifiers, and a Result must carry at least one outcome.</remarks>
    public const string WholeRequest = "request";

    /// <summary>What a message says, in the words its sender wrote.</summary>
    /// <param name="payloadJson">A Request, Clarify or Result payload, or <see langword="null"/>.</param>
    /// <returns>The words, trimmed; empty when there are none or the payload cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// A Request carries its words in <c>ask</c> and a Clarify in <c>text</c>; a Result carries them against its
    /// outcomes, joined here with a space in the order written. The facilitator reads an agent's answer with this.
    /// </para>
    /// <para>
    /// <b>In Core because the broker is what reads it.</b> What an agent said decides whether its answer becomes a line,
    /// and a broker that parsed it differently from the surface a person reviews it on would accept one thing and show
    /// another.
    /// </para>
    /// </remarks>
    public static string Spoken(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var field in (ReadOnlySpan<string>)["ask", "text"])
            {
                if (root.TryGetProperty(field, out var said) && said.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(said.GetString()))
                {
                    return said.GetString()!.Trim();
                }
            }

            if (!root.TryGetProperty("outcomes", out var outcomes) || outcomes.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var words = outcomes.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.Object)
                .Select(o => o.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null)
                .Where(text => !string.IsNullOrWhiteSpace(text));

            return string.Join(" ", words).Trim();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a lone surrogate escape passes json_valid, and reading it as a string throws.
            return string.Empty;
        }
    }

    /// <summary>Reads the outcome a Result gave each item.</summary>
    /// <param name="resultJson">The Result payload.</param>
    /// <param name="outcomes">The item identifiers and outcomes, when the payload is well formed.</param>
    /// <returns>
    /// <see langword="true"/> if the payload has an <c>outcomes</c> array whose every entry names an id and
    /// an outcome the protocol defines.
    /// </returns>
    public static bool TryReadOutcomes(string resultJson, out IReadOnlyList<(string Id, Outcome Outcome)> outcomes)
    {
        outcomes = [];

        try
        {
            using var document = JsonDocument.Parse(resultJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("outcomes", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var read = new List<(string, Outcome)>();

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("outcome", out var word) || word.ValueKind != JsonValueKind.String
                    || !OutcomeTokens.TryParse(word.GetString(), out Outcome outcome))
                {
                    return false;
                }

                read.Add((id.GetString()!, outcome));
            }

            outcomes = read;
            return read.Count > 0;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
