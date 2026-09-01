using System.Reflection;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Broker V1 over a real socket: what it answers, what it bounds, and what it will not carry.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2 is a versioned contract, so the first thing worth asserting is that a client can
/// discover the version at all. Without that a broker of the wrong version and one that is
/// switched off are the same silence, and they want different words in front of a person.
/// </para>
/// <para>
/// The rest is the boundary. These operations read across every pair in a deployment, which is
/// exactly what §10.1 withholds from an agent — so the checks that matter are the ones asserting
/// an agent cannot reach them, and that nothing on the wire carries a project identifier, since
/// §4.1 says the protocol has no concept of a project and must not acquire one.
/// </para>
/// </remarks>
public sealed class BrokerV1Tests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_broker_says_which_version_it_speaks()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var said = await broker.ConnectAsOperator()
            .ImplementsAsync(new Wire.Empty(), cancellationToken: token);

        Assert.Equal(ProtocolVersion.Current, said.ProtocolVersion);
    }

    [Fact]
    public async Task A_listing_answers_before_anybody_knows_an_identifier()
    {
        // The whole reason the operation exists. An identifier is opaque and chosen by an agent
        // (§5.0), so a person cannot type one, and export takes nothing else.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var listed = await broker.ConnectAsOperator().ListConversationsAsync(
            new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.NotNull(listed);
    }

    [Fact]
    public async Task The_broker_bounds_the_page_whatever_is_asked_for()
    {
        // §10.2 makes rule 8.5's argument one level up: a bound the caller chooses is not a
        // bound. Asked for a million, the broker answers with its own ceiling rather than
        // attempting the read.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var listed = await broker.ConnectAsOperator().ListConversationsAsync(
            new Wire.ListConversationsRequest { PageSize = 1_000_000 },
            cancellationToken: token);

        Assert.True(listed.Conversations.Count <= 200);
    }

    [Fact]
    public async Task An_export_carries_the_exchange_states_beside_its_messages()
    {
        // Both or neither. A transcript without them cannot tell an abandoned question from an
        // answered one, which §10.2 calls the most misleading thing a console can do.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var exported = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "nothing-here" },
            cancellationToken: token);

        Assert.Empty(exported.Messages);
        Assert.Empty(exported.Exchanges);
    }

    [Fact]
    public void No_request_in_broker_V1_carries_a_project()
    {
        // §4.1: the protocol has no concept of a project and must not acquire one. Membership is
        // which endpoint the caller connected to, so a field here would hand one broker several
        // projects to tell apart and a WHERE clause somebody can forget. Checked against the
        // generated types rather than the .proto, because the generated types are what a client
        // can actually set.
        Type[] requests =
        [
            typeof(Wire.ListConversationsRequest),
            typeof(Wire.ExportRequest),
            typeof(Wire.InstructRequest),
            typeof(Wire.AgentRequest),
            typeof(Wire.ForceConcludeRequest)
        ];

        var offending = requests
            .SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => name.Contains("Project", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "broker V1 has acquired a project identifier: " + string.Join(", ", offending));
    }

    [Fact]
    public async Task An_agent_cannot_reach_the_operator_surface()
    {
        // The structural half of §10.1. An adapter binds Agents, and Agents has no method that
        // lists another pair's conversations or retires anybody — so this asserts the separation
        // the type split and the service split both exist to make.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var agentSurface = typeof(Wire.Agents.AgentsClient)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Taken from the operator client rather than written out. A hand-kept list checks the
        // operations somebody remembered, and the one that matters is the one they did not: an
        // operation added to both services would be missing from a list and present on the wire,
        // and this test would go on passing while saying it had checked.
        //
        // Names are compared without their generated suffixes -- protoc emits Foo, FooAsync and
        // the callback overloads for one rpc.
        var operatorOnly = typeof(Wire.Operators.OperatorsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name.EndsWith("Async", StringComparison.Ordinal)
                ? m.Name[..^"Async".Length]
                : m.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            operatorOnly.Count > 5,
            "the operator surface did not reflect, so this check would pass by vacuity.");

        var reachable = operatorOnly
            .Where(op => agentSurface.Contains(op) || agentSurface.Contains(op + "Async"))
            .ToList();

        Assert.True(
            reachable.Count == 0,
            "an agent's client can reach operator operations: " + string.Join(", ", reachable));

        // And the operator surface is a different client on a different service, which is the
        // half that survives a refactor of the types.
        Assert.NotEqual(
            typeof(Wire.Agents.AgentsClient), typeof(Wire.Operators.OperatorsClient));
    }

    [Fact]
    public async Task Instructing_an_agent_on_an_unreachable_machine_is_undeliverable_rather_than_lost()
    {
        // §11.5: an agent whose node is not connected cannot be instructed, and must be told so
        // rather than left looking as though it was. Undeliverable and failed are different facts
        // and the remedy differs — the machine is off, and re-sending will not change that.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var agent = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await agent.DisposeAsync();

        try
        {
            var said = await broker.ConnectAsOperator().InstructAsync(
                new Wire.InstructRequest { Agent = "docs@alpha", Text = "have a look at the retry policy" },
                deadline: DateTime.UtcNow.Add(Patience),
                cancellationToken: token);

            Assert.Equal(Wire.InstructionOutcome.Undeliverable, said.Outcome);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unimplemented)
        {
            Assert.Fail("Instruct is declared in broker V1 and must be bound, not left unimplemented.");
        }
    }

    [Fact]
    public async Task An_over_long_instruction_is_refused_in_words_rather_than_by_the_schema()
    {
        // The instructions table carries the same cap the messages table does, and reaching it as
        // a constraint violation hands the operator a failed write to interpret. This is the one
        // refusal somebody can actually act on -- unlike an agent, a person can simply say less.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().InstructAsync(
                new Wire.InstructRequest
                {
                    Agent = "docs@alpha",
                    Text = new string('x', MessageLimits.MaxPayloadCharacters + 1)
                },
                deadline: DateTime.UtcNow.Add(Patience),
                cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
        Assert.Contains("at most", failure.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_participant_that_is_not_a_name_is_refused_rather_than_widening_the_list()
    {
        // A narrow question answered with a wide result, and the caller cannot tell: an empty
        // list invites a second look, a full one reads as the answer. The console says so on its
        // own page, but the console is not the only thing that may ask.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
            await broker.ConnectAsOperator().ListConversationsAsync(
                new Wire.ListConversationsRequest { Participant = "not a name" },
                deadline: DateTime.UtcNow.Add(Patience),
                cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
        Assert.Contains("cannot narrow this list", failure.Status.Detail, StringComparison.Ordinal);
    }
}
