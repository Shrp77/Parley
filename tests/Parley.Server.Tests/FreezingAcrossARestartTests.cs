using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What a start of the broker does to a person's freeze: lifts it, unless the person asked that it outlast one (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>A start is the evidence of a logon or a reboot.</b> Each start gives the broker a number of its own, kept with every
/// freeze, so a freeze an earlier run made is told from one this run made. A freeze meant to bring agents back after a reboot would
/// otherwise stay for ever.
/// </para>
/// <para>
/// <b>Run on a store file</b>, so the second broker is a real restart over the same rows.
/// </para>
/// </remarks>
[Integration]
public sealed class FreezingAcrossARestartTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-freeze-restart").FullName;

    private string Store => Path.Combine(_folder, "parley.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A folder that cannot be removed is the temp directory's to clean.
        }
    }

    private static async Task RegisterAsync(LoopbackBroker broker, string name)
    {
        await using (await AttachedClient.AttachAsync(broker, name, cancellationToken: Ct))
        {
        }

        await broker.WaitUntilDetachedAsync(name, Patience);
    }

    private static async Task<bool> FrozenAsync(LoopbackBroker broker, string name) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Single(r => r.Name == name).FrozenUtc.Length > 0;

    private static Wire.FreezeRequest Freeze(string name, bool keep = false) => new() { Agent = name, KeepFrozen = keep };

    [Fact]
    public async Task A_freeze_lapses_when_the_broker_starts_again()
    {
        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await RegisterAsync(first, "orders@alpha");
            _ = await first.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: Ct);

            Assert.True(await FrozenAsync(first, "orders@alpha"));
        }

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.False(await FrozenAsync(second, "orders@alpha"));
        Assert.NotEqual(WakeOutcome.Frozen, second.Runtime.RequestAttentionForMail(AgentName.Create("orders@alpha"), "after the restart"));
    }

    [Fact]
    public async Task A_freeze_the_person_asked_to_keep_survives_the_broker_starting_again_and_still_stops_a_wake()
    {
        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await RegisterAsync(first, "orders@alpha");
            _ = await first.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha", keep: true), cancellationToken: Ct);
        }

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        var row = (await second.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Single(r => r.Name == "orders@alpha");

        Assert.NotEmpty(row.FrozenUtc);
        Assert.True(row.KeepFrozen);
        Assert.Equal(WakeOutcome.Frozen, second.Runtime.RequestAttentionForMail(AgentName.Create("orders@alpha"), "after the restart"));
    }

    [Fact]
    public async Task A_freeze_made_in_this_run_is_not_lifted_by_anything_this_run_does()
    {
        await using var broker = await LoopbackBroker.StartOnAsync(Store, Ct);
        await RegisterAsync(broker, "orders@alpha");
        _ = await broker.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: Ct);

        _ = await broker.Runtime.TickAsync(Ct);

        Assert.True(await FrozenAsync(broker, "orders@alpha"));
    }

    [Fact]
    public async Task Unfreezing_forgets_the_choice_to_keep_so_the_next_freeze_is_an_ordinary_one()
    {
        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await RegisterAsync(first, "orders@alpha");
            var console = first.ConnectAsOperator();

            _ = await console.FreezeAsync(Freeze("orders@alpha", keep: true), cancellationToken: Ct);
            _ = await console.UnfreezeAsync(new Wire.AgentRequest { Agent = "orders@alpha" }, cancellationToken: Ct);
            _ = await console.FreezeAsync(Freeze("orders@alpha"), cancellationToken: Ct);
        }

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.False(await FrozenAsync(second, "orders@alpha"));
    }

    [Fact]
    public async Task Only_the_unkept_freeze_is_lifted_and_each_start_has_a_run_of_its_own()
    {
        string firstRun;

        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            firstRun = first.Runtime.RunId;
            await RegisterAsync(first, "orders@alpha");
            await RegisterAsync(first, "billing@alpha");
            _ = await first.ConnectAsOperator().FreezeAsync(Freeze("orders@alpha"), cancellationToken: Ct);
            _ = await first.ConnectAsOperator().FreezeAsync(Freeze("billing@alpha", keep: true), cancellationToken: Ct);
        }

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct);

        Assert.NotEqual(firstRun, second.Runtime.RunId);
        Assert.Equal(36, second.Runtime.RunId.Length);
        Assert.False(await FrozenAsync(second, "orders@alpha"));
        Assert.True(await FrozenAsync(second, "billing@alpha"));
    }
}
