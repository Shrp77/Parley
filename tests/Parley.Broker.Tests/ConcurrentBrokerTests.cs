using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Two brokers on two connections against one database file.
/// </summary>
/// <remarks>
/// <para>
/// Not the deployment SPEC.md §4.1 describes — that has one broker per project, each with its
/// own database — but it is the arrangement §13.1 contemplates when it names the hashing
/// algorithm so that <em>independent implementations sharing a database</em> agree on what a
/// duplicate is. Sharing is admitted, so contention has to resolve into a refusal an agent can
/// read rather than an exception or a half-written exchange.
/// </para>
/// <para>
/// These use a real file, because an in-memory database is private to the connection that
/// opened it and would make two brokers into two universes that could not possibly collide —
/// a test that passes by never running the case.
/// </para>
/// </remarks>
public sealed class ConcurrentBrokerTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    private const string Question =
        """{"text":"is the price gross or net?","consequences":"If gross I stop adding tax; if net I keep adding it."}""";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"parley-concurrent-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
                // Best effort. The temp directory is not what is under test, and a file still
                // held open by SQLite's cleanup would otherwise fail a test that had passed.
            }
        }
    }

    private async Task<(Broker One, Broker Two)> TwoBrokersAsync(
        Microsoft.Data.Sqlite.SqliteConnection one,
        Microsoft.Data.Sqlite.SqliteConnection two)
    {
        var agents = new AgentRepository(one);
        foreach (var name in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken);
        }

        var brokerOne = new Broker(one, BrokerOptions.Default);
        var brokerTwo = new Broker(two, BrokerOptions.Default);
        await brokerOne.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        await brokerTwo.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        return (brokerOne, brokerTwo);
    }

    [Fact]
    public async Task The_same_question_asked_at_once_by_both_opens_exactly_one_exchange()
    {
        // The window this probes is the one the transaction opened up: an ask now holds the
        // write lock across two writes rather than one, so there is more of it to collide with.
        // It resolves into the ordinary refusal — busy_timeout makes the loser wait for the
        // lock, and the distinctness index of §7.1 refuses it when it gets there.
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        var results = await Task.WhenAll(
            Task.Run(() => Ask(brokerOne)),
            Task.Run(() => Ask(brokerTwo)));

        Assert.Single(results, r => r == "opened");
        Assert.Single(results, r => r == nameof(OpenRejection.DuplicateQuestion));

        async Task<string> Ask(Broker broker)
        {
            var asked = await broker.AskAsync(
                Orders, Billing, Model, Question, T0.AddMinutes(1),
                cancellationToken: TestContext.Current.CancellationToken);
            return asked.Succeeded ? "opened" : asked.Rejection.ToString();
        }
    }

    [Fact]
    public async Task The_loser_of_a_race_writes_no_question()
    {
        // The refusal has to leave nothing behind, or the losing broker has done exactly what
        // the two-commit version did: contributed a question with no exchange, or an exchange
        // with no question.
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        await Task.WhenAll(
            Task.Run(() => brokerOne.AskAsync(
                Orders, Billing, Model, Question, T0.AddMinutes(1),
                cancellationToken: TestContext.Current.CancellationToken)),
            Task.Run(() => brokerTwo.AskAsync(
                Orders, Billing, Model, Question, T0.AddMinutes(1),
                cancellationToken: TestContext.Current.CancellationToken)));

        var open = await new ExchangeRepository(one).GetOpenAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(open);

        var pending = await new MessageRepository(one).GetPendingAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(pending);
    }

    [Fact]
    public async Task Two_answers_to_one_exchange_close_it_once()
    {
        // Both read inside their own write-locked transaction, so they serialise: the second
        // reads the row the first committed and its transition is refused against a closed
        // exchange. What must not happen is two answers landing on one exchange, which would
        // put two settlements in the asker's inbox for one question.
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        var asked = await brokerOne.AskAsync(
            Orders, Billing, Model, Question, T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(asked.Succeeded);

        var results = await Task.WhenAll(
            Task.Run(() => Answer(brokerOne)),
            Task.Run(() => Answer(brokerTwo)));

        Assert.Single(results, r => r);

        // The loser is refused against a closed exchange, not reported as stale: it read the
        // row the winner had already committed. That is why SayOutcome carries no staleness —
        // see SayAsync.
        Assert.Single(results, r => !r);

        var inbox = (await brokerOne.DrainInboxAsync(
            Orders, T0.AddMinutes(5), cancellationToken: TestContext.Current.CancellationToken)).Messages;
        Assert.Single(inbox);

        async Task<bool> Answer(Broker broker)
        {
            var answered = await broker.AnswerAsync(
                asked.Exchange!.Id, Billing, Model,
                """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
                T0.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken);
            return answered.Succeeded;
        }
    }
}
