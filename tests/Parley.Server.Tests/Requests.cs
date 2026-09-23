using System.Text.Json.Nodes;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Well-formed Requests and Results as they cross the wire.
/// </summary>
/// <remarks>
/// One place rather than a copy per file, because storage refuses a malformed payload and the refusal arrives
/// over the wire as <c>InvalidArgument</c> — a test that wrote its own shape would fail as a transport fault on
/// the day the shape moves, which reads as a broken wire rather than a stale fixture.
/// </remarks>
internal static class Requests
{
    /// <summary>A Request payload: the ask, and nothing else (SPEC.md §5.1).</summary>
    /// <param name="text">What it asks. Varying it is how a test makes two Requests distinct.</param>
    /// <returns>The payload.</returns>
    public static string Payload(string text) => new JsonObject { ["ask"] = text }.ToJsonString();

    /// <summary>A Request on the wire.</summary>
    /// <param name="from">The requester.</param>
    /// <param name="to">The receiver: a whole name or a bare role.</param>
    /// <param name="text">What it asks.</param>
    /// <param name="conversation">The conversation named, or null to let the broker assign one.</param>
    /// <returns>The request.</returns>
    public static Wire.OpenRequest Open(string from, string to, string text, string? conversation = null) =>
        new()
        {
            From = from,
            To = to,
            ResponderModel = "claude-opus-5",
            PayloadJson = Payload(text),
            ConversationId = conversation ?? string.Empty
        };

    /// <summary>A Result answering the exchange whole, under the one item §5.3 reserves.</summary>
    /// <param name="from">The receiver.</param>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="outcome">What came of it.</param>
    /// <returns>The request.</returns>
    public static Wire.ResultRequest Result(string from, long exchangeId, string outcome = "answered") =>
        new()
        {
            From = from,
            ExchangeId = exchangeId,
            ResponderModel = "claude-opus-5",
            PayloadJson = new JsonObject
            {
                ["outcomes"] = new JsonArray(
                    new JsonObject { ["id"] = "request", ["outcome"] = outcome, ["text"] = "billing owns it" })
            }.ToJsonString()
        };
}
