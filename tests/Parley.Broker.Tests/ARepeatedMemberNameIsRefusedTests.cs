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
        // A NUL ending a name is the same member to SQLite and a different one to System.Text.Json: a decoy (security run 4).
        """{"outcomes":[{"id":"request","outcome\u0000":"answered","outcome":"blocked","text":"t"}]}""",
        """{"outcomes\u0000":[{"id":"request","outcome":"answered","text":"t"}],"outcomes":[{"id":"request","outcome":"blocked","text":"t"}]}""",
        """{"outcomes":[{"id":"request","outcome":"blocked","text":"t","blocker\u0000":"conflict"}]}""",
        """{"outcomes":[{"id":"request","outcome":"answered","outcome\ud800":"x","text":"t"}]}""",
        // Text that cannot be decoded: valid to the store, a throw to the broker (security run 5): an emoji cut in half.
        """{"outcomes":[{"id":"request","outcome":"blocked","blocker":"permission","text":"cut \ud83d"}]}""",
        """{"outcomes":[{"id":"request","outcome":"done","text":"t","sources":["\udc00"]}]}""",
    ];

    [Theory]
    [MemberData(nameof(RepeatedInAResult))]
    public async Task A_result_naming_a_member_ambiguously_is_refused_whole(string payload)
    {
        var exchange = await OpenAsync();
        var before = Messages();

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ResultAsync(exchange, Billing, Requests.Model, payload, T0.AddMinutes(1), Ct));

        Assert.Contains("refused whole", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Messages());
        Assert.Equal("OPEN", _connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = @exchange;", new { exchange }));
    }

    [Fact]
    public async Task A_name_ending_in_a_nul_is_refused_on_a_request_and_a_clarify_too()
    {
        var exchange = await OpenAsync();
        var before = Messages();

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.RequestAsync(Orders, Billing, Requests.Model, """{"ask\u0000":"real","ask":" "}""", T0.AddMinutes(1), "pricing", Ct));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ClarifyAsync(exchange, Billing, Requests.Model, """{"text\u0000":"real","text":" "}""", T0.AddMinutes(1), Ct));

        Assert.Equal(before, Messages());
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
    public async Task A_payload_with_a_raw_nul_after_its_document_is_refused_on_a_request_and_a_clarify()
    {
        // Review, security lens run 5 (0.63.2): SQLite stops reading a text at a raw NUL and System.Text.Json refuses it, so a
        // repeated name followed by a NUL and a tail was valid to the store and unreadable to the guard, which left it to the
        // store: stored first-wins and cut where the NUL was.
        var exchange = await OpenAsync();
        var before = Messages();
        var tail = "\0" + new string('A', 400);

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.RequestAsync(Orders, Billing, Requests.Model, """{"ask":"first","ask":"   "}""" + tail, T0.AddMinutes(1), "pricing", Ct));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ClarifyAsync(exchange, Billing, Requests.Model, """{"text":"a","text":"b"}""" + tail, T0.AddMinutes(1), Ct));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ResultAsync(exchange, Billing, Requests.Model, """{"outcomes":[{"id":"request","outcome":"done","text":"t"}]}""" + tail, T0.AddMinutes(1), Ct));

        Assert.Equal(before, Messages());
    }

    [Fact]
    public async Task A_result_whose_words_end_in_half_an_emoji_is_refused_and_not_closed_blocked_and_shown_done()
    {
        var exchange = await OpenAsync();
        var before = Messages();

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => _broker.ResultAsync(
                exchange, Billing, Requests.Model,
                """{"outcomes":[{"id":"request","outcome":"blocked","blocker":"permission","text":"cut \ud83d"}]}""",
                T0.AddMinutes(1), Ct));

        Assert.Contains("no half of an emoji", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Messages());
        Assert.Equal("OPEN", _connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = @exchange;", new { exchange }));
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
    [InlineData("""{"a\u0000":1}""", true)]
    [InlineData("""{"a":{"b":[{"c\u0000":1}]}}""", true)]
    [InlineData("""{"a\ud800":1}""", true)]
    [InlineData("""{"a":"value ending in a nul\u0000"}""", true)]
    [InlineData("""{"a":"x\ud800"}""", true)]
    [InlineData("""{"a":["fine","cut \ud83d"]}""", true)]
    [InlineData("""{"a":"a whole emoji \ud83d\ude00"}""", false)]
    public void The_check_reads_what_the_broker_reads(string json, bool ambiguous) =>
        Assert.Equal(ambiguous, PayloadShape.HasAmbiguousName(json));

    [Fact]
    public void A_lone_surrogate_in_the_string_itself_is_refused()
    {
        // Not a JSON escape: the .NET string holds half an emoji, which JsonDocument.Parse throws ArgumentException on. An attribute
        // argument cannot carry one (metadata is UTF-8, so the compiler turns it into U+FFFD), which is why this is not a Theory row.
        var half = ((char)0xD83D).ToString();

        Assert.True(PayloadShape.HasAmbiguousName("{\"a\":\"cut " + half + "\"}"));
        Assert.True(PayloadShape.HasAmbiguousName("{\"a" + half + "\":1}"));
    }
}
