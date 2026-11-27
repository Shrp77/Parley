using Dapper;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That what the Inbox reads of an exchange (its state, outcome, times, whether the person is in it, the other party, the
/// newest message and the newest Result) is the same from the summary export as from the whole one, over random stores
/// with moves, clarifies, Results of every outcome, closes, force-concludes and settled questions.
/// </summary>
/// <remarks>
/// The Inbox's projection is copied here as a plain function, because this project does not reference the client. A change to
/// what the Inbox reads of an exchange must change both.
/// </remarks>
[Integration]
[Slow]
public sealed class ASummaryExportReadsLikeTheWholeOneTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<object?> Project(ConversationExport export, AgentName me)
    {
        var rows = new List<object?>();

        foreach (var ex in export.Exchanges.OrderBy(e => e.ExchangeId))
        {
            var on = export.Messages.Where(m => m.ExchangeId == ex.ExchangeId).OrderBy(m => m.Id).ToList();
            var mine = on.Any(m => m.From == me || m.To == me);
            var other = on.Select(m => m.From == me ? m.To : m.From).Where(a => a != me).Cast<AgentName?>().FirstOrDefault();
            var res = on.LastOrDefault(m => m.Type == MessageType.Result);

            rows.Add((ex.ExchangeId, ex.State, ex.Outcome, ex.ClosedUtc, ex.OpenedUtc, on.Count > 0, mine, on.LastOrDefault()?.Id,
                on.LastOrDefault()?.ConversationId, (mine && ex.State != ExchangeState.Open ? other?.ToString() : null), res?.Id, res?.PayloadJson, res?.From.ToString()));
        }

        return rows;
    }

    [Fact]
    public async Task The_projection_the_inbox_reads_is_the_same_from_the_summary()
    {
        var checkedRows = 0;
        AgentName[] all = [Orders, Billing, Person];

        for (var seed = 0; seed < 60; seed++)
        {
            var rnd = new Random(seed);
            using var connection = ParleyDatabase.OpenInMemory();
            connection.Execute(
                """
                INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-05T00:00:00.0000000Z');
                INSERT INTO agents (name, adapter, tier, status) VALUES
                    ('orders@test', 'claude-code', 'full', 'active'),
                    ('billing@test', 'claude-code', 'full', 'active'),
                    ('human-user@test', 'parley-console', 'pull', 'active');
                """);

            var broker = new Broker(connection, BrokerOptions.Default);
            broker.ServeFrom(T0);
            var console = new OperatorConsole(connection);
            var ids = new List<long>();

            for (var step = 0; step < 40; step++)
            {
                var now = T0.AddMinutes(step + 1);
                var conversation = rnd.Next(4) == 0 ? "other" : "talk";
                var a = all[rnd.Next(3)];
                var b = all[rnd.Next(3)];

                try
                {
                    switch (rnd.Next(9))
                    {
                        case 0 or 1 or 2 when a != b:
                            var sent = await broker.RequestAsync(a, b, Requests.Model, Requests.Request("q" + step), now, conversation, Ct);

                            if (sent.Exchange is { } exchange)
                            {
                                ids.Add(exchange.Id);
                            }

                            break;
                        case 3 or 4 when ids.Count > 0:
                            _ = await broker.ClarifyAsync(ids[rnd.Next(ids.Count)], a, Requests.Model, Requests.Clarify("c" + step), now, Ct);
                            break;
                        case 5 or 6 when ids.Count > 0:
                            var outcome = rnd.Next(3) switch { 0 => "answered", 1 => "acknowledged", _ => "blocked:missing_information" };
                            IReadOnlyList<long>? settles = rnd.Next(2) == 0 ? [.. ids.Where(_ => rnd.Next(3) == 0).Take(2)] : null;
                            _ = await broker.ResultAsync(
                                ids[rnd.Next(ids.Count)], a, Requests.Model, Requests.Result(("request", outcome)), now, Ct, settles);
                            break;
                        case 7 when ids.Count > 0:
                            _ = await console.CloseRequestAsync(ids[rnd.Next(ids.Count)], "no", now, Ct);
                            break;
                        case 8:
                            if (rnd.Next(3) == 0)
                            {
                                _ = await console.ForceConcludeAsync(conversation, "done", now, Ct);
                            }
                            else if (ids.Count > 0)
                            {
                                var from = rnd.Next(2) == 0 ? "talk" : "other";

                                _ = await console.MoveExchangesAsync(
                                    from, from == "talk" ? "other" : "talk", false, [.. ids.Where(_ => rnd.Next(2) == 0)], now, Ct);
                            }

                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // A refused act: the store is what it was, and the comparison below is still of that store.
                }
            }

            foreach (var conversation in new[] { "talk", "other" })
            {
                var full = await console.ExportAsync(conversation, Ct);
                var summary = await console.ExportAsync(conversation, Ct, latestOnly: true);

                foreach (var me in all)
                {
                    var whole = Project(full, me);
                    var small = Project(summary, me);

                    Assert.True(
                        whole.SequenceEqual(small),
                        $"seed {seed}, {conversation}, as {me}:\nWHOLE\n{string.Join("\n", whole)}\nSUMMARY\n{string.Join("\n", small)}");
                }

                checkedRows += full.Exchanges.Count;
            }
        }

        Assert.True(checkedRows > 300, $"only {checkedRows} exchanges were compared");
    }
}
