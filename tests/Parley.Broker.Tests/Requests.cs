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

    /// <summary>A Request: the ask, and nothing else (§5.1).</summary>
    /// <param name="text">What is asked. Varying it is how a test makes two Requests distinct.</param>
    /// <returns>The payload.</returns>
    public static string Request(string text = "is the price gross or net?") =>
        new JsonObject { ["ask"] = text }.ToJsonString();

    // `OldRequestOf` stood here: a Request in the shape 0.5 and 0.6 stored, for tests that answered
    // one by its items. Its only route into a store was to build one at schema 10 and migrate, and
    // there are no intermediate schemas since the eleven migrations were folded into one on
    // 2026-09-26. Every test that used it went with them. Recorded rather than silently removed,
    // because the half of §5.3 it exercised has not changed -- an exchange opened before 0.7 is
    // still answered by the identifiers its Request carried, and `ItemIdBoundTests` is what holds
    // that now.

    /// <summary>A Result giving each named item an outcome; a blocked one is written <c>blocked:kind</c>.</summary>
    /// <param name="outcomes">The items and what came of each, or none for <c>q1</c> answered.</param>
    /// <returns>The payload.</returns>
    public static string Result(params (string Id, string Outcome)[] outcomes)
    {
        var entries = new JsonArray();

        foreach (var (id, outcome) in outcomes.Length == 0 ? [(PayloadShape.WholeRequest, "answered")] : outcomes)
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
    /// <param name="adapter">Its adapter; <see cref="PersonSeat.Adapter"/> makes it a person.</param>
    public static void Register(SqliteConnection connection, AgentName name, string adapter) =>
        new AgentRepository(connection).UpsertAsync(
            new AgentRecord(name, adapter, AgentTier.Full, null, null, null, false, AgentStatus.Active),
            CancellationToken.None).GetAwaiter().GetResult();
}
