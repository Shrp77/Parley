using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker.Tests;

/// <summary>
/// Well-formed Requests, Clarifies and Results, and registering the participants who send them.
/// </summary>
/// <remarks>
/// One place rather than a copy per file, because storage refuses a malformed payload (§9.3). A test that
/// wrote its own would be testing whether its author remembered the shape, and the day the shape moves every
/// copy would find out separately.
/// </remarks>
internal static class Requests
{
    /// <summary>The responder every test message claims.</summary>
    public const string Model = "claude-opus-5";

    /// <summary>A <c>done_when</c> long enough for rule 8.1.</summary>
    public const string DoneWhen = "the price is stated as gross or net of tax";

    /// <summary>A Request with one item.</summary>
    /// <param name="text">What the item asks. Varying it is how a test makes two Requests distinct.</param>
    /// <param name="kind">Work or a question.</param>
    /// <param name="id">The item's identifier.</param>
    /// <param name="doneWhen">What counts as finished.</param>
    /// <returns>The payload.</returns>
    public static string Request(
        string text = "is the price gross or net?", string kind = "question", string id = "q1", string doneWhen = DoneWhen) =>
        new JsonObject
        {
            ["intent"] = "deciding where tax is added to an invoice",
            ["items"] = new JsonArray(new JsonObject { ["id"] = id, ["kind"] = kind, ["text"] = text }),
            ["done_when"] = doneWhen
        }.ToJsonString();

    /// <summary>A Request with several question items.</summary>
    /// <param name="subject">What it is about, which keeps two distinct.</param>
    /// <param name="ids">The items' identifiers, in order.</param>
    /// <returns>The payload.</returns>
    public static string RequestOf(string subject, params string[] ids)
    {
        var items = new JsonArray();

        foreach (var id in ids)
        {
            items.Add(new JsonObject { ["id"] = id, ["kind"] = "question", ["text"] = $"{subject}: {id}" });
        }

        return new JsonObject
        {
            ["intent"] = "deciding where tax is added to an invoice",
            ["items"] = items,
            ["done_when"] = DoneWhen
        }.ToJsonString();
    }

    /// <summary>A Result giving each named item an outcome; a blocked one is written <c>blocked:kind</c>.</summary>
    /// <param name="outcomes">The items and what came of each, or none for <c>q1</c> answered.</param>
    /// <returns>The payload.</returns>
    public static string Result(params (string Id, string Outcome)[] outcomes)
    {
        var entries = new JsonArray();

        foreach (var (id, outcome) in outcomes.Length == 0 ? [("q1", "answered")] : outcomes)
        {
            var parts = outcome.Split(':', 2);
            var entry = new JsonObject { ["id"] = id, ["outcome"] = parts[0], ["text"] = "net of tax" };

            if (parts.Length == 2)
            {
                entry["blocker"] = parts[1];
            }

            entries.Add(entry);
        }

        return new JsonObject { ["outcomes"] = entries }.ToJsonString();
    }

    /// <summary>A clarification, or the reply to one.</summary>
    /// <param name="text">What it says.</param>
    /// <returns>The payload.</returns>
    public static string Clarify(string text) => new JsonObject { ["text"] = text }.ToJsonString();

    /// <summary>Registers agents, active.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="names">The agents.</param>
    public static void Register(SqliteConnection connection, params AgentName[] names)
    {
        foreach (var name in names)
        {
            Register(connection, name, "test");
        }
    }

    /// <summary>Registers one participant under a given adapter.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="name">The participant.</param>
    /// <param name="adapter">Its adapter; <see cref="Seats.Adapter"/> makes it a person.</param>
    public static void Register(SqliteConnection connection, AgentName name, string adapter) =>
        new AgentRepository(connection).UpsertAsync(
            new AgentRecord(name, adapter, AgentTier.Full, null, null, null, false, AgentStatus.Active),
            CancellationToken.None).GetAwaiter().GetResult();
}
