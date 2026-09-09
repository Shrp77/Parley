using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What the readback tells a caller about agents it did not ask about.
/// </summary>
/// <remarks>
/// <para>
/// <b>A listing is an oracle if its silences differ.</b> This one can be asked about any name, and
/// the interesting question is whether "no such agent", "an agent with nothing said to it" and "an
/// agent whose instructions were removed" come back distinguishable. On this surface the answer
/// hardly matters — §10.2 hands the same caller the whole registry — but the reasoning is worth
/// pinning, because the day somebody narrows this surface is the day it starts mattering, and
/// nobody will re-derive it then.
/// </para>
/// <para>
/// The retire-versus-purge half is the one with teeth. §14 keeps a retired agent's history and
/// §10.2's purge destroys it; an operator who retires an agent and then cannot find what they told
/// it has been given a deletion they did not ask for.
/// </para>
/// </remarks>
public sealed class InstructionDisclosureTests
{
    private static async Task<long> ToldAsync(
        LoopbackBroker broker, string agent, string text, CancellationToken token)
    {
        var attached = await AttachedClient.AttachAsync(broker, agent, "C:/repo", token);
        await attached.DisposeAsync();

        var said = await broker.ConnectAsOperator().InstructAsync(
            new Wire.InstructRequest { Agent = agent, Text = text },
            cancellationToken: token);

        return said.InstructionId;
    }

    [Fact]
    public async Task Retiring_an_agent_keeps_what_the_operator_told_it()
    {
        // §14 keeps a retired agent's history: retirement stops routing, it does not erase. An
        // operator who retires an agent and loses the record of what they asked it has been given a
        // deletion they did not choose -- and `reinstate` exists, so the agent may be back tomorrow.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var id = await ToldAsync(broker, "orders@alpha", "before retiring", token);

        var operators = broker.ConnectAsOperator();

        var retired = await operators.RetireAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        Assert.True(retired.Changed);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token);

        Assert.Equal("before retiring", Assert.Single(read.Instructions).Text);
    }

    [Fact]
    public async Task Purging_an_agent_takes_them_with_it()
    {
        // The other half, and the contrast is the point: §10.2's purge destroys an agent and
        // everything that referred to it. The two operations are named apart precisely so nobody
        // reaches for the wrong one, and their effects here must differ too.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var id = await ToldAsync(broker, "orders@alpha", "before purging", token);

        var operators = broker.ConnectAsOperator();

        _ = await operators.PurgeAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token);

        Assert.Empty(read.Instructions);
    }

    [Fact]
    public async Task An_agent_nobody_registered_answers_the_same_as_one_told_nothing()
    {
        // The oracle question. Both come back empty, so a caller learns nothing from the shape of
        // the silence -- which is the property to keep if this surface is ever narrowed.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var attached = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo", token);
        await attached.DisposeAsync();

        var operators = broker.ConnectAsOperator();

        var registered = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { Agent = "orders@alpha" }, cancellationToken: token);

        var imaginary = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { Agent = "nobody@alpha" }, cancellationToken: token);

        Assert.Empty(registered.Instructions);
        Assert.Empty(imaginary.Instructions);
        Assert.Equal(registered.NextPageToken, imaginary.NextPageToken);
    }

    [Fact]
    public async Task An_unnarrowed_listing_carries_every_agent_and_that_is_deliberate()
    {
        // Non-vacuity for the three above, and a statement of the disclosure this surface makes on
        // purpose: §10.2 is the operator's, and an operator reading their own instructions to their
        // own agents is not a leak. The narrowing exists for convenience, not containment -- which
        // is why the oracle question above is recorded as "does not matter here" rather than
        // "cannot happen".
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        _ = await ToldAsync(broker, "orders@alpha", "one", token);
        _ = await ToldAsync(broker, "billing@alpha", "two", token);

        var read = await broker.ConnectAsOperator().InstructionsAsync(
            new Wire.InstructionsRequest(), cancellationToken: token);

        Assert.Equal(2, read.Instructions.Count);
    }

    [Fact]
    public async Task Reinstating_a_retired_agent_finds_its_instructions_where_they_were()
    {
        // The sequence, which is what an operator actually does: retire something noisy, change
        // their mind, and expect the history to be intact. §10.2 says reinstate withdraws a
        // retirement, and a history that came back different would make retirement lossy after all.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var id = await ToldAsync(broker, "orders@alpha", "kept throughout", token);

        var operators = broker.ConnectAsOperator();

        _ = await operators.RetireAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        _ = await operators.ReinstateAsync(
            new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: token);

        var read = await operators.InstructionsAsync(
            new Wire.InstructionsRequest { InstructionId = id },
            cancellationToken: token);

        Assert.Equal("kept throughout", Assert.Single(read.Instructions).Text);
    }
}
