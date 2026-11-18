using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// That reading the project's past is one thing in the document, on the wire and in the schema, and that no statement of the
/// old rule survives (SPEC.md §5.8, §10.1, §13).
/// </summary>
/// <remarks>
/// A rule reversed in one place and kept in another is the failure this guards: the spec said there is no history operation,
/// the owner decided on 2026-11-17 that there is, and a sentence left behind in the spec, the docs or a tool's description
/// would tell an agent the opposite of what it can do.
/// </remarks>
[Integration]
public sealed class HistoryReadsAgreeTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string Read(params string[] under) => File.ReadAllText(Path.Combine([Root(), .. under]));

    private static string Flat(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void The_spec_names_both_operations_in_the_agent_table()
    {
        var spec = Read("SPEC.md");

        Assert.Contains("| `read_exchange(exchange_id, after?)` |", spec, StringComparison.Ordinal);
        Assert.Contains("| `search_history(words, participant?", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_proto_has_the_two_rpcs_and_the_person_reads_what_was_read()
    {
        var proto = Read("proto", "parley.proto");

        Assert.Contains("rpc ReadExchange(ReadExchangeRequest) returns (ReadExchangeReply);", proto, StringComparison.Ordinal);
        Assert.Contains("rpc SearchHistory(SearchHistoryRequest) returns (SearchHistoryReply);", proto, StringComparison.Ordinal);
        Assert.Contains("repeated HistoryReadRow reads = 11;", proto, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_in_the_spec_is_the_migrations_table()
    {
        var spec = Read("SPEC.md");
        var migration = Read("src", "Parley.Store", "Migrations", "009_history_reads.sql");

        Assert.Contains("CREATE TABLE history_reads (", spec, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE history_reads (", migration, StringComparison.Ordinal);
        Assert.Contains("kind         TEXT    NOT NULL CHECK (kind IN ('exchange', 'search'))", spec, StringComparison.Ordinal);
        Assert.Contains("kind         TEXT    NOT NULL CHECK (kind IN ('exchange', 'search'))", migration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no history and no search operation")]
    [InlineData("There is deliberately no history")]
    [InlineData("No agent can read the log or search it")]
    [InlineData("exposes no history or search operation")]
    [InlineData("cannot read the log because nothing exposes it")]
    [InlineData("No agent can read the message log or search it")]
    [InlineData("An agent sees only its own exchanges")]
    [InlineData("gives a participant no way to read exchanges")]
    [InlineData("Never conversation history")]
    public void No_statement_of_the_old_rule_is_left(string old)
    {
        var places = new[]
        {
            Read("SPEC.md"),
            Read("README.md"),
            Read("docs", "security.md"),
            Read("docs", "concepts.md"),
            Read("docs", "why-parley.md"),
            Read("docs", "operating.md"),
            Read("CLAUDE.md"),
            Read(".specify", "memory", "constitution.md"),
            Read("src", "Parley.Mcp", "ParleyTools.cs"),
            Read("src", "Parley.Client", "Rendering", "RoomRoster.cs"),
        };

        Assert.All(places, text => Assert.DoesNotContain(old, Flat(text).Replace("**", string.Empty, StringComparison.Ordinal).Replace("// ", string.Empty, StringComparison.Ordinal).Replace("/// ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_constitution_permits_it_and_says_how()
    {
        var constitution = Flat(Read(".specify", "memory", "constitution.md"));

        Assert.Contains("An agent may read the project's exchange history, and only read it", constitution, StringComparison.Ordinal);
        Assert.DoesNotContain("Never conversation history,", constitution, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spec_says_what_stays_forbidden()
    {
        var spec = Flat(Read("SPEC.md"));

        Assert.Contains("A roadmap is not an exchange and neither operation returns one", spec, StringComparison.Ordinal);
        Assert.Contains("Turn information is never in a reply", spec, StringComparison.Ordinal);
        Assert.Contains("It changes nothing", spec, StringComparison.Ordinal);
        Assert.Contains("It is seen.", spec, StringComparison.Ordinal);
    }
}
