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
/// with no <c>done_when</c>, a Result naming no item. A test that wrote its own would be testing whether its
/// author remembered the shape, and the day the shape moves every copy would have to find out separately.
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

    /// <summary>A <c>done_when</c> long enough for rule 8.1.</summary>
    public const string DoneWhen = "the invoice total is stated as gross or net of tax";

    /// <summary>The conversation tests use when they have no reason to name one.</summary>
    public const string Conversation = "invoice-rounding";

    /// <summary>A Request payload with one item.</summary>
    /// <param name="text">What the item asks. Varying it is how a test makes two Requests distinct.</param>
    /// <param name="kind">Work or a question.</param>
    /// <param name="id">The item's identifier.</param>
    /// <returns>The payload as stored.</returns>
    public static string Request(string text = "is the invoice total gross or net?", string kind = "question", string id = "q1") =>
        new JsonObject
        {
            ["intent"] = "deciding where tax is added to an invoice",
            ["items"] = new JsonArray(new JsonObject { ["id"] = id, ["kind"] = kind, ["text"] = text }),
            ["done_when"] = DoneWhen
        }.ToJsonString();

    /// <summary>A Request payload with several question items.</summary>
    /// <param name="subject">What the Request is about, which keeps two of them distinct.</param>
    /// <param name="ids">The items' identifiers, in order.</param>
    /// <returns>The payload as stored.</returns>
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
