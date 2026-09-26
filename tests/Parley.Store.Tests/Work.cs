using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store.Tests;

/// <summary>
/// Well-formed Requests and Results, and the few steps every test takes to put one in the store.
/// </summary>
/// <remarks>
/// <para>
/// One place rather than a copy per file, because storage refuses a malformed payload (§9.3): a Request
/// with no ask, a Result naming no item. A test that wrote its own would be testing whether its author
/// remembered the shape — and the day the shape moved, which is what 0.7 did to it, every copy would have
/// had to find out separately. This one changed, and the suites did not.
/// </para>
/// <para>
/// The steps are the ones the broker takes, in its order: admit the exchange, then write the Request on it.
/// A test wanting the exchange without its message calls <see cref="ExchangeRepository.TryOpenAsync"/>
/// itself and says why.
/// </para>
/// </remarks>
internal static class Work
{
    /// <summary>The responder every test message claims.</summary>
    public const string Model = "claude-opus-5";

    /// <summary>The conversation tests use when they have no reason to name one.</summary>
    public const string Conversation = "invoice-rounding";

    /// <summary>A Request payload: the ask, and nothing else (§5.1).</summary>
    /// <param name="text">What is asked. Varying it is how a test makes two Requests distinct.</param>
    /// <returns>The payload as stored.</returns>
    public static string Request(string text = "is the invoice total gross or net?") =>
        new JsonObject { ["ask"] = text }.ToJsonString();

    // `OldRequest` and `DoneWhen` stood here: a Request in the shape 0.5 and 0.6 stored. Their only
    // route into a store was to build one at schema 10 and migrate, and there are no intermediate
    // schemas since the eleven migrations were folded into one on 2026-09-26. The surfaces that
    // render a pre-0.7 payload are still tested -- `OldShapeRendersTests` and `OldShapeOnThePageTests`
    // each build the payload themselves, because rendering one needs no store at all.

    /// <summary>A Request payload whose ask names several things, for the Results that answer several items.</summary>
    /// <param name="subject">What the Request is about, which keeps two of them distinct.</param>
    /// <param name="ids">What the Result will name its outcomes. The ask mentions them so the pair reads together.</param>
    /// <returns>The payload as stored.</returns>
    /// <remarks>
    /// A 0.7 Request has no items, and storage never made a Result's outcomes match any. These suites are about the
    /// result tables, so the Request is the ask it would now be and the Result names what the test needs.
    /// </remarks>
    public static string RequestOf(string subject, params string[] ids) =>
        new JsonObject { ["ask"] = $"{subject}: {string.Join(", ", ids)}" }.ToJsonString();

    /// <summary>A Result payload giving each named item an outcome.</summary>
    /// <param name="outcomes">Each item and what came of it; a blocked outcome is written <c>blocked:kind</c>.</param>
    /// <returns>The payload as stored.</returns>
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

    /// <summary>Registers agents, active.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="names">The agents.</param>
    public static void Register(SqliteConnection connection, params AgentName[] names)
    {
        var agents = new AgentRepository(connection);

        foreach (var name in names)
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>Admits an exchange and writes its Request, as the broker does.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="from">The requester.</param>
    /// <param name="to">The receiver.</param>
    /// <param name="at">When.</param>
    /// <param name="payload">The Request, or <see langword="null"/> for the default one.</param>
    /// <param name="conversationId">The conversation it goes into.</param>
    /// <param name="policy">The terms, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The exchange and the Request message.</returns>
    /// <exception cref="InvalidOperationException">Admission refused it.</exception>
    public static async Task<(Exchange Exchange, Message Request)> OpenAsync(
        SqliteConnection connection,
        AgentName from,
        AgentName to,
        DateTimeOffset at,
        string? payload = null,
        string conversationId = Conversation,
        ExchangePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        var request = payload ?? Request();

        var opened = await new ExchangeRepository(connection).TryOpenAsync(
            from, to, at, MessageIdentity.ComputeRequest(from, to, request), conversationId, policy,
            cancellationToken: cancellationToken);

        if (!opened.Succeeded)
        {
            throw new InvalidOperationException($"The fixture's Request was refused: {opened.Rejection}.");
        }

        var written = await new MessageRepository(connection).AppendAsync(
            opened.Exchange!.Id, MessageType.Request, from, to, Model, request, at,
            cancellationToken: cancellationToken);

        return (opened.Exchange, written.Message);
    }

    /// <summary>Sends the receiver's Result and closes the exchange, as the broker does.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="exchange">The open exchange, as last saved.</param>
    /// <param name="at">When.</param>
    /// <param name="outcome">The rolled-up outcome.</param>
    /// <param name="payload">The Result, or <see langword="null"/> for one answering <c>q1</c>.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The Result message.</returns>
    /// <exception cref="InvalidOperationException">The exchange would not close.</exception>
    public static async Task<Message> CloseAsync(
        SqliteConnection connection,
        Exchange exchange,
        DateTimeOffset at,
        ExchangeOutcome outcome = ExchangeOutcome.Complete,
        string? payload = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var written = await new MessageRepository(connection).AppendAsync(
            exchange.Id, MessageType.Result, exchange.Receiver, exchange.Requester, Model,
            payload ?? Result(), at, cancellationToken: cancellationToken);

        if (!exchange.Close(exchange.Receiver, at, outcome).Succeeded
            || !await new ExchangeRepository(connection).SaveAsync(exchange, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException($"Exchange {exchange.Id} would not close.");
        }

        return written.Message;
    }
}
