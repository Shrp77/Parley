using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What an operator sees when they ask what the work came to, and what needs them now.
/// </summary>
/// <remarks>
/// <para>
/// <c>results</c> is where a blocker reaches a person. An agent reports blocked only after pursuing it (§8),
/// so what arrives here is the one thing no agent in the deployment could resolve, and the report has to say
/// on what — a count of "blocked" alone sends the person to read every Result to find out.
/// </para>
/// <para>
/// <c>flags</c> is the other half. Nothing in 0.5 times out, so work that has stopped moving stays open until
/// somebody acts, and this is how somebody learns there is something to act on.
/// </para>
/// </remarks>
public sealed class ResultsAndFlagsReportTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    /// <summary>Long before the broker's own clock, so anything uncollected is past every threshold.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "some-model-9";

    private const string Request =
        """{"intent":"where does price live","items":[{"id":"q1","kind":"question","text":"gross or net?"},{"id":"w1","kind":"work","text":"stamp the rate on refunds"}],"done_when":"the price is stated and refunds carry the rate"}""";

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    public ResultsAndFlagsReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
        _commands = new OperatorCommands(_broker.Operators, _output);
    }

    public async ValueTask DisposeAsync()
    {
        // The broker first: it borrowed this connection and its shutdown writes to it.
        await _broker.DisposeAsync();

        _connection.Dispose();
        _output.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Printed => _output.ToString();

    private async Task<(Broker.Broker Broker, long Exchange)> SentAsync()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var sent = await broker.RequestAsync(Orders, Billing, Model, Request, T0, "pricing", Ct);
        Assert.True(sent.Exchange is not null, $"expected admission, got {sent.Admission.Rejection}");

        return (broker, sent.Exchange.Id);
    }

    [Fact]
    public async Task No_results_says_so_rather_than_printing_an_empty_table()
    {
        Assert.Equal(0, await _commands.ResultsAsync(Ct));

        Assert.Contains("No results have been sent", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocker_is_reported_with_what_blocked_it()
    {
        var (broker, exchange) = await SentAsync();

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.True((await broker.ResultAsync(
            exchange, Billing, Model,
            """{"outcomes":[{"id":"q1","outcome":"answered","text":"net"},{"id":"w1","outcome":"blocked","blocker":"missing_information","text":"HERRING-TEXT: which refund table is canonical"}]}""",
            T0.AddMinutes(2), cancellationToken: Ct)).Succeeded);

        Assert.Equal(0, await _commands.ResultsAsync(Ct));

        Assert.Contains("billing@test", Printed, StringComparison.Ordinal);
        Assert.Contains("answered", Printed, StringComparison.Ordinal);
        Assert.Contains("blocked", Printed, StringComparison.Ordinal);
        Assert.Contains("missing_information", Printed, StringComparison.Ordinal);

        // Counts and kinds, never what was said. An operator reading this is outside the §13 envelope
        // that marks a participant's words as somebody else's.
        Assert.DoesNotContain("HERRING-TEXT", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_flagged_says_so()
    {
        Assert.Equal(0, await _commands.FlagsAsync(Ct));

        Assert.Contains("Nothing is flagged", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_nobody_collected_is_flagged_with_who_holds_it()
    {
        // The Request was sent months before the broker's clock and nothing collected it. It is not
        // closed for anyone (§5.3); it is put in front of a person, naming the exchange and the agent
        // whose move it is, because that is what they act on.
        var (_, exchange) = await SentAsync();

        Assert.Equal(0, await _commands.FlagsAsync(Ct));

        Assert.Contains("pricing", Printed, StringComparison.Ordinal);
        Assert.Contains($"exchange {exchange}", Printed, StringComparison.Ordinal);
        Assert.Contains("billing@test", Printed, StringComparison.Ordinal);
        Assert.Contains("has not collected it", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("gross or net", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_waiting_only_on_a_vote_is_flagged_without_an_exchange()
    {
        // Found by the review gate, lens 3 run 3.6. Orders proposed closure while billing held the work, billing sent
        // its Result and never voted. Nothing is open, so the line names the conversation and who has not answered,
        // and no exchange: there is none to go and look at.
        var (broker, exchange) = await SentAsync();

        Assert.False((await broker.ConcludeAsync("pricing", Orders, finished: true, T0.AddMinutes(1), Ct)).Concluded);

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.True((await broker.ResultAsync(
            exchange, Billing, Model,
            """{"outcomes":[{"id":"q1","outcome":"answered","text":"net"},{"id":"w1","outcome":"done","text":"stamped"}]}""",
            T0.AddMinutes(3), cancellationToken: Ct)).Succeeded);

        Assert.Equal(0, await _commands.FlagsAsync(Ct));

        Assert.Contains("pricing", Printed, StringComparison.Ordinal);
        Assert.Contains("closing it is waiting on billing@test", Printed, StringComparison.Ordinal);
        Assert.Contains("orders@test", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain($"exchange {exchange}", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("has not collected", Printed, StringComparison.Ordinal);
    }
}
