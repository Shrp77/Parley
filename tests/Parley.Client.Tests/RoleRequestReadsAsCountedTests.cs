using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a person reading a conversation sees how many of the agents a Request to a role reached have answered
/// (SPEC.md §5.5), once, beneath its first Request.
/// </summary>
/// <remarks>
/// Counted from how each member closed: complete and blocked are answers, a member the person closed is not, and an
/// open one is still waited on. A Result whose words retention pruned still counts, because nothing reads the words.
/// </remarks>
[Unit]
public sealed class RoleRequestReadsAsCountedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Docs = AgentName.Create("docs@alpha");

    private static Message Request(long id, long exchange, string to) =>
        new(id, exchange, MessageType.Request, Docs, AgentName.Create(to), string.Empty, """{"text":"what about DNS?"}""", "c-1", T0, null);

    private static ConversationExport Export(params ExchangeSnapshot[] exchanges) =>
        new(
            [Request(1, 10, "infra@alpha"), Request(2, 11, "infra@beta"), Request(3, 12, "infra@gamma"), Request(4, 13, "infra@delta")],
            exchanges);

    [Fact]
    public void The_first_request_of_a_role_says_how_many_answered_and_the_rest_say_nothing()
    {
        var export = Export(
            new ExchangeSnapshot(10, ExchangeState.Closed, T0, T0, ExchangeOutcome.Complete, null, "g"),
            new ExchangeSnapshot(11, ExchangeState.Closed, T0, T0, ExchangeOutcome.Blocked, null, "g"),
            new ExchangeSnapshot(12, ExchangeState.Closed, T0, T0, ExchangeOutcome.Closed, null, "g"),
            new ExchangeSnapshot(13, ExchangeState.Open, T0, null, null, null, "g"));

        Assert.Equal(Reading.RoleRequestLine("infra", 4, 2), Reading.RoleRequest(export.Messages[0], export));
        Assert.All(export.Messages.Skip(1), message => Assert.Null(Reading.RoleRequest(message, export)));
        Assert.Contains(Reading.Lines(export), line => line.Kind == LineKind.Annotation && line.Text.Contains("2 of 4 answered", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_export_says_it_beneath_the_request_too()
    {
        // The export is a person's record taken away (lens 3 run 86 found its line held by nothing).
        var export = Export(
            new ExchangeSnapshot(10, ExchangeState.Closed, T0, T0, ExchangeOutcome.Complete, null, "g"),
            new ExchangeSnapshot(11, ExchangeState.Open, T0, null, null, null, "g"),
            new ExchangeSnapshot(12, ExchangeState.Open, T0, null, null, null, "g"),
            new ExchangeSnapshot(13, ExchangeState.Open, T0, null, null, null, "g"));

        var written = Transcript.Write("c-1", "alpha", export.Messages, export.Exchanges);

        Assert.Contains(Reading.RoleRequestLine("infra", 4, 1), written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_to_one_agent_says_nothing()
    {
        var export = Export(new ExchangeSnapshot(10, ExchangeState.Closed, T0, T0, ExchangeOutcome.Complete));

        Assert.Null(Reading.RoleRequest(export.Messages[0], export));
    }
}
