using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Launching a server: what it registers, what it attaches, and what it refuses.
/// </summary>
/// <remarks>
/// The start-up path is the one the person launching a repository experiences, and everything it
/// gets wrong it gets wrong before there is any protocol to report it through. These are about
/// the three outcomes that path has — it worked, it worked for the first time, or the agent has
/// been withdrawn — and about the last one being distinguishable.
/// </remarks>
public sealed class BrokerSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static async Task<AgentRecord?> LoadAsync(string path, string name, CancellationToken ct)
    {
        using var connection = ParleyDatabase.OpenAndMigrate(path);
        var agents = new AgentRepository(connection);
        var all = await agents.GetAllAsync(ct);

        return all.FirstOrDefault(a => a.Name.Value == name);
    }

    [Fact]
    public async Task A_first_launch_creates_the_database_and_registers_the_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        // The ordinary first run of a project: no file, no registry, nobody has ever asked
        // anything. Before self-registration this could not happen at all — the broker refuses
        // an unregistered agent, and nothing existed to register one.
        Assert.False(File.Exists(fixture.DatabasePath));

        await fixture.LaunchAsync("orders", ct);

        var record = await LoadAsync(fixture.DatabasePath, "orders", ct);

        Assert.NotNull(record);
        Assert.Equal(BrokerSession.Adapter, record.Adapter);
        Assert.Equal(AgentStatus.Live, record.Status);
    }

    [Fact]
    public async Task The_declared_tier_is_pull_because_that_is_what_mcp_can_do()
    {
        // An honest claim, not a conservative one. Nothing in MCP pushes a message into a model's
        // context between tool calls, so inbound traffic is seen when the agent drains its inbox
        // and at no other time. Declaring Full would have §11.1's routing push to something that
        // cannot be pushed to, and the messages would sit unseen with the broker believing
        // otherwise.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        await fixture.LaunchAsync("orders", ct);

        var record = await LoadAsync(fixture.DatabasePath, "orders", ct);

        Assert.Equal(AgentTier.Pull, record!.Tier);
    }

    [Fact]
    public async Task A_server_registers_itself_and_nobody_else()
    {
        // The property that keeps §12.3 from being a hole in §13. An agent may write its own
        // registration because its own name is fixed by the launch environment, which is exactly
        // what it can already act as. A third name appearing here would mean a launch had
        // created an identity nobody chose.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        await fixture.LaunchAsync("orders", ct);

        using var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath);
        var all = await new AgentRepository(connection).GetAllAsync(ct);

        Assert.Equal(["orders"], all.Select(a => a.Name.Value));
    }

    [Fact]
    public async Task Relaunching_does_not_duplicate_the_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (first, _) = await fixture.LaunchAsync("orders", ct);
        await fixture.ShutDownAsync(first);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.LaunchAsync("orders", ct);

        using var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath);
        var all = await new AgentRepository(connection).GetAllAsync(ct);

        Assert.Single(all);
    }

    [Fact]
    public async Task Launching_over_an_operators_registration_leaves_it_exactly_alone()
    {
        // §12.3: self-registration does not replace the manifest, and here that is enforced
        // rather than asserted. An upsert on every launch would rewrite billing — registered
        // Headless with the model and endpoint the broker needs in order to invoke it — as a
        // Pull agent with no configuration at all, the first time anybody opened that
        // repository in an editor. The broker would then have no way to reach billing while
        // its session was closed, which is the entire point of the headless tier, and nothing
        // would say so.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var manifest = new AgentRecord(
            AgentName.Create("billing"),
            "vendor-api",
            AgentTier.Headless,
            "/src/acme-billing",
            SessionRef: null,
            """{"model":"some-model-9","endpoint":"https://api.example.invalid"}""",
            LastSeenUtc: null,
            AgentStatus.Registered);

        using (var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath))
        {
            await new AgentRepository(connection).UpsertAsync(manifest, ct);
        }

        await fixture.LaunchAsync("billing", ct);

        var record = await LoadAsync(fixture.DatabasePath, "billing", ct);

        Assert.Equal(manifest.Adapter, record!.Adapter);
        Assert.Equal(manifest.Tier, record.Tier);
        Assert.Equal(manifest.Workspace, record.Workspace);
        Assert.Equal(manifest.AdapterConfig, record.AdapterConfig);

        // Liveness is the one thing the launch does say, and it says it whoever registered.
        Assert.Equal(AgentStatus.Live, record.Status);
    }

    [Fact]
    public async Task Relaunching_a_retired_agent_is_refused_rather_than_reviving_it()
    {
        // The one that makes self-registration safe to run on every launch. §14: retirement is
        // cleared only by an explicit operator action, never by registration — and a server
        // starting is registration. Without this an operator who withdrew an agent would find it
        // live again the moment somebody opened that repository, which is a control that does
        // not hold.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (first, _) = await fixture.LaunchAsync("orders", ct);
        await fixture.ShutDownAsync(first);

        using (var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath))
        {
            await new AgentRepository(connection).RetireAsync(AgentName.Create("orders"), ct);
        }

        var ex = await Assert.ThrowsAsync<AgentRetiredException>(
            () => fixture.LaunchAsync("orders", ct));

        Assert.Equal("orders", ex.Agent);

        var record = await LoadAsync(fixture.DatabasePath, "orders", ct);
        Assert.Equal(AgentStatus.Retired, record!.Status);
    }

    [Fact]
    public async Task A_reinstated_agent_can_launch_again()
    {
        // The operator's way back, and the reason refusing a retired agent is safe to do at
        // start-up rather than something gentler. Retirement is reversible by the person who
        // caused it and by nobody else — including the agent, which cannot reverse it by trying
        // repeatedly to start.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (first, _) = await fixture.LaunchAsync("orders", ct);
        await fixture.ShutDownAsync(first);

        using (var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath))
        {
            await new AgentRepository(connection).RetireAsync(AgentName.Create("orders"), ct);
        }

        await Assert.ThrowsAsync<AgentRetiredException>(() => fixture.LaunchAsync("orders", ct));

        using (var connection = ParleyDatabase.OpenAndMigrate(fixture.DatabasePath))
        {
            await new AgentRepository(connection).ReinstateAsync(AgentName.Create("orders"), ct);
        }

        await fixture.LaunchAsync("orders", ct);

        Assert.Equal(AgentStatus.Live, (await LoadAsync(fixture.DatabasePath, "orders", ct))!.Status);
    }

    [Fact]
    public async Task Shutting_down_marks_the_agent_offline()
    {
        // Cosmetic — nothing routes on offline — but it is what makes parley_agents show who is
        // actually at their desk, and a registry that reports everybody live is one nobody reads.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (session, _) = await fixture.LaunchAsync("orders", ct);
        Assert.Equal(AgentStatus.Live, (await LoadAsync(fixture.DatabasePath, "orders", ct))!.Status);

        await fixture.ShutDownAsync(session);

        Assert.Equal(AgentStatus.Offline, (await LoadAsync(fixture.DatabasePath, "orders", ct))!.Status);
    }

    [Fact]
    public async Task Shutting_down_twice_does_not_throw()
    {
        // Reached in the ordinary case rather than by misuse: Program disposes in a finally, and
        // a host that has already stopped may have disposed the session on the way. The second
        // call must be a no-op, not an ObjectDisposedException on the way out of a clean run.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (session, _) = await fixture.LaunchAsync("orders", ct);

        await fixture.ShutDownAsync(session);
        await session.DisposeAsync();
    }
}
