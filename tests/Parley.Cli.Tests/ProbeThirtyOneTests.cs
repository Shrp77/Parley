using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 20: what the writer will put into an identity.
/// </summary>
/// <remarks>
/// <para>
/// Every earlier run asked what comes <i>out of</i> a file and where it may safely go. This asks the
/// opposite: what this program will write <i>into</i> one. The launch configuration is the whole of
/// who an agent is — SPEC.md §16 makes the launch environment the only thing that decides it — and the
/// owns line is charged against every peer's working memory and capped by a CHECK constraint in the
/// broker's own schema.
/// </para>
/// <para>
/// Both rules exist and both live in <c>EnrollmentPlan.Validate</c>, which the bulk command calls
/// before it writes anything. <c>AddAsync</c> is the other entry point and is public. A rule enforced
/// in one path and not the other is this repository's recorded shape — <i>one rule applied to half the
/// output is worse than either applied whole</i> — so the question is whether the guarantee is the
/// code's or the callers'.
/// </para>
/// </remarks>
public sealed class ProbeThirtyOneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe31-" + Path.GetRandomFileName());

    public ProbeThirtyOneTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private Task<Enrolment.Outcome> EnrolAsync(string agent, string owns) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, agent, owns, Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "alpha",
            Path.Combine(_root, "node.json"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    private async Task<string?> WrittenAgentAsync()
    {
        if (!File.Exists(LaunchFile))
        {
            return null;
        }

        var servers = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"];

        return servers?.AsObject().FirstOrDefault().Value?["env"]?["PARLEY_AGENT"]?.GetValue<string>();
    }

    [Theory]
    [InlineData("orders\nbilling")]
    [InlineData("orders billing")]
    [InlineData("Orders")]
    [InlineData("orders@here")]
    [InlineData("9orders")]
    [InlineData("")]
    public async Task A_name_no_agent_could_hold_is_refused_rather_than_written(string agent)
    {
        // Identity is the one thing this protocol makes unforgeable, and it comes from this file.
        // A name written here that no `AgentName` can parse produces a repository that announces
        // itself and never appears on any roster — which reads, from every peer's side, exactly like
        // an agent that was never enrolled.
        var failure = await Record.ExceptionAsync(() => EnrolAsync(agent, "Carts and totals."));

        Assert.NotNull(failure);
        Assert.Null(await WrittenAgentAsync());
    }

    [Fact]
    public async Task A_long_owns_line_is_written_because_the_schema_now_accepts_one()
    {
        // **This probe asserted the opposite until 0.33.0**, and the reason it is inverted rather
        // than deleted is that what it is really about has not changed: what the enrolment writes
        // must be what the schema accepts. A launch configuration carrying a line the broker
        // refuses produces an agent refused at registration, which is a failure a long way from
        // the person who caused it -- and that is just as true of a line refused for being long as
        // of one written when nothing objects.
        //
        // The bound came off on the owner's decision of 2026-09-26 so that an agent asked what it
        // owns can answer in two halves. The character rule is what still refuses, and the test
        // below this one is the probe for it.
        await EnrolAsync("orders", new string('x', 5_000));

        Assert.Equal("orders", await WrittenAgentAsync());
    }

    [Fact]
    public async Task An_owns_line_that_can_break_its_own_roster_row_is_refused_rather_than_written()
    {
        // The rule the manifest's own guidance states out loud: a roster entry able to break its own
        // line can imitate the entries around it. It is enforced where the bulk command validates,
        // and this is the other way in.
        var failure = await Record.ExceptionAsync(
            () => EnrolAsync("orders", "Carts.[2JSomething else entirely."));

        Assert.NotNull(failure);
        Assert.Null(await WrittenAgentAsync());
    }

    [Fact]
    public async Task The_catalogue_is_keyed_by_something_a_node_can_actually_look_up()
    {
        // **Why the name rule is not merely tidiness, asked of the key 018 gave this file.** It used to
        // be whatever role string this writer was handed, and a node found an entry by `agent.Role` —
        // so a key under a *qualified* name was never found and never started: standing consent
        // recorded in a form nothing could act on, with nothing said. The adapter accepts either
        // spelling in `PARLEY_AGENT`, which is what made the qualified one look admissible.
        //
        // The key is an identity now, which cannot be mis-spelled into something unlookupable, and the
        // question becomes whether the node can still get from a name to it. It can only do that by
        // reading what the checkout declares — so the two files have to agree, and a catalogue keyed by
        // an identity the repository does not carry is the same defect in its new shape.
        await Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: true)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "alpha",
            Path.Combine(_root, "node.json"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var agents = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "node.json"), TestContext.Current.CancellationToken))!["agents"]!;

        var key = Assert.Single(agents).Key;

        Assert.True(
            AgentIdentity.TryRead(key, out var keyed),
            $"the catalogue is keyed '{key}', which no node will ever look up");

        Assert.True(
            LaunchConfiguration.TryRead(Workspace, out var declared, out var problem), problem);

        Assert.Equal(keyed, declared.Identity);

        // And the name the repository claims is still one an agent can be called, because that is what
        // a node resolves to this key and what the broker refuses to let anything forge.
        var claimed = Assert.Single(declared.Memberships);

        Assert.True(
            EnrollmentPlan.IsWellFormedName(claimed.Name),
            $"the repository claims '{claimed.Name}', which is not a name an agent can hold");
    }

    [Fact]
    public async Task An_ordinary_enrolment_still_writes()
    {
        // The control. Refusing everything would satisfy all of the above.
        await EnrolAsync("orders", "Carts and totals. Not payment capture.");

        Assert.Equal("orders", await WrittenAgentAsync());
    }
}
