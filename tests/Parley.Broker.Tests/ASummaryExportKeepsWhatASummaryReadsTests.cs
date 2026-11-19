using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That the summary export (<c>latest_only</c>) holds the newest message and the newest Result of every exchange of a
/// conversation, with the same exchange states, and nothing else (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// Review, performance lens (0.62.6): the Inbox exported every active conversation whole on each render, and with the person
/// in 200 conversations of 2,500 messages that took 8 s, 2 GB, and held the broker's gate for every agent for as long.
/// </remarks>
[Integration]
public sealed class ASummaryExportKeepsWhatASummaryReadsTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_summary_is_the_newest_message_and_newest_result_of_each_exchange_and_the_same_states()
    {
        Requests.Register(_connection, Orders, Billing);

        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);
        await broker.StartAsync(Billing, T0, Ct);

        // One exchange of four messages ending in a result, one open with a clarify, one with only its request.
        var closed = (await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request("first"), T0, "talk", Ct)).Exchange!.Id;
        _ = await broker.ClarifyAsync(closed, Billing, Requests.Model, Requests.Clarify("which?"), T0.AddMinutes(1), Ct);
        _ = await broker.ClarifyAsync(closed, Orders, Requests.Model, Requests.Clarify("this one"), T0.AddMinutes(2), Ct);
        _ = await broker.ResultAsync(closed, Billing, Requests.Model, Requests.Result(("request", "done")), T0.AddMinutes(3), Ct);

        var open = (await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request("second"), T0.AddMinutes(4), "talk", Ct)).Exchange!.Id;
        _ = await broker.ClarifyAsync(open, Billing, Requests.Model, Requests.Clarify("which?"), T0.AddMinutes(5), Ct);

        _ = await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request("third"), T0.AddMinutes(6), "talk", Ct);

        var console = new OperatorConsole(_connection);
        var full = await console.ExportAsync("talk", Ct);
        var summary = await console.ExportAsync("talk", Ct, latestOnly: true);

        long NewestOf(long exchange, Func<Message, bool> keep) =>
            full.Messages.Where(m => m.ExchangeId == exchange).Where(keep).Max(m => m.Id);

        var expected = full.Messages.Select(m => m.ExchangeId).Distinct()
            .SelectMany(exchange => new[] { NewestOf(exchange, _ => true) }
                .Concat(full.Messages.Any(m => m.ExchangeId == exchange && m.Type == MessageType.Result)
                    ? [NewestOf(exchange, m => m.Type == MessageType.Result)]
                    : []))
            .Distinct()
            .Order()
            .ToList();

        Assert.True(full.Messages.Count > expected.Count, "the conversation has to hold more than the summary does");
        Assert.Equal(expected, summary.Messages.Select(m => m.Id).ToList());
        Assert.Equal(full.Exchanges, summary.Exchanges);
        Assert.Equal(full.Messages.Where(m => expected.Contains(m.Id)), summary.Messages);
    }
}
