using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a line the person types to an agent is a new Request, and answers none of the agent's open questions
/// (SPEC.md §5.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-05.</b> An agent had asked the person several things in one conversation; the
/// person replied to the newest with a question of their own, and the box filed it as the Result of an unrelated
/// request two days old — whichever the wait-for graph listed first. A Result asks nothing back, so the agent said
/// nothing, and the questions it had really asked stayed open.
/// </para>
/// <para>
/// <b>Driven, and over a real socket</b>, because what is asserted is what the broker recorded after Enter. Each test
/// fails against 0.40.1, where the line became a Result on one of the three.
/// </para>
/// </remarks>
public sealed class LineIsARequestOverTheWireTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public LineIsARequestOverTheWireTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_line_to_an_agent_with_three_questions_open_answers_none_of_them()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await AskedThreeTimesAsync(driving, directory, seats, ct);

        var asked = await OpenFromOrdersAsync(ct);

        Assert.Equal(3, asked.Count);

        shell.Line.Written = "Before I answer: what does the export need the schema for?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);
        var person = shell.Held!.Seat.Name;

        // All three of orders' questions are still open: nothing the person typed was filed as an answer.
        Assert.All(asked, id => Assert.Equal(
            ExchangeState.Open, export!.Exchanges.Single(e => e.ExchangeId == id).State));

        // And the line is a new Request from the person, which orders now owes a reply on.
        var request = Assert.Single(
            export!.Messages,
            m => m.From == person && m.PayloadJson.Contains("what does the export need", StringComparison.Ordinal));

        Assert.Equal(MessageType.Request, request.Type);
        Assert.DoesNotContain(asked, id => id == request.ExchangeId);
        Assert.Equal(ExchangeState.Open, export.Exchanges.Single(e => e.ExchangeId == request.ExchangeId).State);
    }

    [Fact]
    public async Task One_open_question_is_no_reason_to_guess_either()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, questions: 1, ct);

        var asked = Assert.Single(await OpenFromOrdersAsync(ct));

        shell.Line.Written = "Green on my side.";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.Equal(ExchangeState.Open, export!.Exchanges.Single(e => e.ExchangeId == asked).State);
        Assert.Contains(
            export.Messages,
            m => m.Type == MessageType.Request && m.PayloadJson.Contains("Green on my side", StringComparison.Ordinal));
        Assert.DoesNotContain(export.Messages, m => m.Type == MessageType.Result);
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<IReadOnlyList<long>> OpenFromOrdersAsync(CancellationToken cancellationToken)
    {
        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, cancellationToken);

        return [.. export!.Messages
            .Where(m => m.Type == MessageType.Request && m.From == Orders)
            .Select(m => m.ExchangeId)];
    }

    private Task<Shell> AskedThreeTimesAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats, CancellationToken cancellationToken) =>
        SeatedAsync(driving, directory, seats, questions: 3, cancellationToken);

    private async Task<Shell> SeatedAsync(
        Driving.Session driving,
        BrokerDirectory directory,
        SeatBook seats,
        int questions,
        CancellationToken cancellationToken)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(cancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.NotNull(shell.Held);

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, cancellationToken);

        foreach (var (asked, at) in new[] { "which invoice schema?", "gross or net?", "who owns refunds?" }
            .Take(questions)
            .Select((asked, at) => (asked, at)))
        {
            var sent = await broker.RequestAsync(
                Orders, shell.Held!.Seat.Name, "model-a", $$"""{"ask":"{{asked}}"}""",
                T0.AddMinutes(at + 1), Conversation, cancellationToken);

            Assert.True(sent.Exchange is not null, $"the fixture's request was refused: {sent.Admission.Rejection}");
        }

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        shell.Line.Written = string.Empty;
        await shell.Settled;

        return shell;
    }
}
