using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a payload writing one member name twice in one object is refused whole, in every message (SPEC.md §13).
/// </summary>
/// <remarks>
/// Review, security lens run 3 (0.62.9): SQLite's <c>json_extract</c> takes the first of a repeated name and
/// <c>System.Text.Json</c> the last, so <c>{"outcome":"answered","outcome":"blocked"}</c> passed the trigger that a blocked
/// outcome names its blocker, then closed the exchange as Blocked with no blocker in the broker's roll-up. The reverse order
/// was refused by the trigger, which is why only first-valid, last-other worked.
/// </remarks>
[Integration]
public sealed class ARepeatedMemberNameIsRefusedTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;

    public ARepeatedMemberNameIsRefusedTests()
    {
        Requests.Register(_connection, Orders, Billing);
        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();
        _broker.StartAsync(Billing, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> OpenAsync() =>
        (await _broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request("is it gross?"), T0, "pricing", Ct)).Exchange!.Id;

    private long Messages() => _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM messages;");

    public static TheoryData<string> RepeatedInAResult =>
    [
        """{"outcomes":[{"id":"request","outcome":"answered","outcome":"blocked","text":"t"}]}""",
        """{"outcomes":[{"id":"request","outcome":"blocked","outcome":"answered","text":"t"}]}""",
        """{"outcomes":[{"id":"request","id":"other","outcome":"done","text":"t"}]}""",
        """{"outcomes":[{"id":"request","outcome":"done","text":"a","text":"b"}]}""",
        """{"outcomes":[{"id":"request","outcome":"done","text":"t"}],"outcomes":[{"id":"request","outcome":"blocked","text":"t"}]}""",
        """{"outcomes":[{"id":"request","outcome":"done","text":"t","sources":["billing@test"],"sources":["orders@test"]}]}""",
    ];

    [Theory]
    [MemberData(nameof(RepeatedInAResult))]
    public async Task A_result_naming_a_member_twice_is_refused_whole_in_either_order(string payload)
    {
        var exchange = await OpenAsync();
        var before = Messages();

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ResultAsync(exchange, Billing, Requests.Model, payload, T0.AddMinutes(1), Ct));

        Assert.Contains("names each member of its JSON once", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Messages());
        Assert.Equal("OPEN", _connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = @exchange;", new { exchange }));
    }

    [Fact]
    public async Task A_request_a_clarify_and_a_role_request_naming_a_member_twice_are_refused_too()
    {
        var exchange = await OpenAsync();
        var before = Messages();

        const string Twice = """{"ask":"SAFE","ask":"EVIL"}""";
        const string ClarifyTwice = """{"text":"SAFE","text":"EVIL"}""";

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.RequestAsync(Orders, Billing, Requests.Model, Twice, T0.AddMinutes(1), "pricing", Ct));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.RequestRoleAsync(Orders, "billing", Requests.Model, Twice, T0.AddMinutes(1), "pricing", Ct));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ClarifyAsync(exchange, Billing, Requests.Model, ClarifyTwice, T0.AddMinutes(1), Ct));

        Assert.Equal(before, Messages());
    }

    [Fact]
    public async Task The_same_name_in_different_objects_is_not_a_repeat()
    {
        var exchange = await OpenAsync();

        var sent = await _broker.ResultAsync(
            exchange, Billing, Requests.Model,
            """{"outcomes":[{"id":"request","outcome":"done","text":"a"}],"note":{"text":"a"},"other":{"text":"b"}}""",
            T0.AddMinutes(1), Ct);

        Assert.True(sent.Succeeded);
    }

    [Theory]
    [InlineData("""{"ask":"x"}""", false)]
    [InlineData("""{"a":1,"a":2}""", true)]
    [InlineData("""{"a":{"b":1,"b":2}}""", true)]
    [InlineData("""{"a":[{"b":1},{"b":2}]}""", false)]
    [InlineData("""[{"a":1,"a":2}]""", true)]
    [InlineData("not json at all", false)]
    [InlineData("{\"a\":1,\"A\":2}", false)]
    public void The_check_reads_what_the_broker_reads(string json, bool repeated) =>
        Assert.Equal(repeated, PayloadShape.HasRepeatedName(json));
}
