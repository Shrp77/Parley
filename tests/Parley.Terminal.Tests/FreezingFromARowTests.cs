using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That an agent's row offers the freeze its standing calls for, describes it with the projects it reaches, and does
/// nothing until the second choice is taken (SPEC.md §14, §10.2 <c>freeze</c>, <c>unfreeze</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case presses the keys and reads the broker.</b> The menu's labels are private to the surface, so they are
/// written here as a person reads them: a label that changed without the act changing is then a failure here, and so is
/// the reverse.
/// </para>
/// <para>
/// <b>Two projects, each with its own broker</b>, because one process serves every project an agent is in: a freeze
/// that reached only the project in view would end the process and let the other project's work start it again.
/// </para>
/// <para>
/// The description opens from the work queue, after each project has been read, so the key that asks for it is pressed
/// and the surface awaited before anything is drawn (see <c>Driving.Session.PressThenSettleAsync</c>).
/// </para>
/// </remarks>
[Integration]
public sealed class FreezingFromARowTests
{
    private const string KeepItRunning = "Keep it running";
    private const string LeaveItFrozen = "Leave it frozen";

    private static readonly string Freeze = "Freeze " + Seated.Orders.Value;
    private static readonly string FreezeAndKeep = Freeze + ", and keep it frozen when the broker starts again";
    private static readonly string Unfreeze = "Unfreeze " + Seated.Orders.Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_that_is_not_frozen_is_offered_both_freezes_and_no_unfreeze()
    {
        await using var seated = await OnAgentsAsync();

        var offered = Open(seated, Seated.Orders.Value);

        Assert.Contains(Freeze, offered);
        Assert.Contains(FreezeAndKeep, offered);
        Assert.DoesNotContain(offered, label => label.StartsWith("Unfreeze", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("billing@test", false, Standing.Frozen)]
    [InlineData("billing@test", true, Standing.FrozenKept)]
    [InlineData("orders@test", false, Standing.Freezing)]
    public async Task A_frozen_agent_is_offered_unfreeze_and_neither_freeze_in_each_of_the_three_ways_it_reads(
        string agent, bool kept, string standing)
    {
        // The fixture's first agent has registered, so it reads as still stopping; its second never has, so it reads as stopped.
        await using var seated = await OnAgentsAsync();

        Assert.True((await seated.Operators.FreezeAsync(Parley.Core.AgentName.Create(agent), kept, Ct)).Changed);
        await AgainAsync(seated);

        var offered = Open(seated, agent);

        // The row reads as the freeze it is, so each case is its own phrase and not another's under a different name.
        Assert.Equal(standing, seated.Shell.Agents.SelectedStanding!.Standing);
        Assert.Contains("Unfreeze " + agent, offered);
        Assert.DoesNotContain(offered, label => label.StartsWith("Freeze", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retired_agent_is_offered_neither_frozen_or_not(bool frozenFirst)
    {
        await using var seated = await OnAgentsAsync();

        if (frozenFirst)
        {
            Assert.True((await seated.Operators.FreezeAsync(Seated.Billing, cancellationToken: Ct)).Changed);
        }

        Assert.True(await seated.Operators.RetireAsync(Seated.Billing, Ct));
        await AgainAsync(seated);

        var offered = Open(seated, Seated.Billing.Value);

        Assert.True(seated.Shell.Agents.SelectedStanding!.Retired, "the fixture's agent does not read as retired");
        Assert.NotEmpty(offered);
        Assert.DoesNotContain(offered, label => label.Contains("reeze", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Freezing_is_described_with_the_projects_it_reaches_and_keeping_it_running_changes_nothing()
    {
        await using var seated = await OnAgentsAsync(withBeta: true);

        await ChooseAsync(seated, Seated.Orders.Value, Freeze);

        var describing = (ChoiceDialog)seated.Session.Find(Freeze);

        Assert.Equal([KeepItRunning, Freeze], Choices(describing));
        Assert.Contains($"This reaches {Walking.Alpha.Value}, {Seated.Beta.Value}.", describing.Described, StringComparison.Ordinal);
        Assert.Contains(KeepItRunning, seated.Session.Text(), StringComparison.Ordinal);

        // The first choice is the one the cursor opens on, and it is the one that does nothing.
        Assert.Equal(KeepItRunning, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        Assert.False(seated.Session.Has(Freeze), "the description stayed open");
        Assert.False(await FrozenAsync(seated.Operators));
        Assert.False(await FrozenAsync(seated.BetaOperators));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Choosing_the_freeze_freezes_it_in_every_project_and_keeps_it_only_when_that_entry_was_chosen(bool keep)
    {
        await using var seated = await OnAgentsAsync(withBeta: true);
        var act = keep ? FreezeAndKeep : Freeze;

        await ChooseAsync(seated, Seated.Orders.Value, act);

        var describing = (ChoiceDialog)seated.Session.Find(act);

        Assert.Equal([KeepItRunning, act], Choices(describing));
        Assert.StartsWith(Parley.Client.Rendering.FreezingWords.Freezing(Seated.Orders.Value, keep), describing.Described, StringComparison.Ordinal);

        seated.Session.Row(describing.Choices, act);
        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        foreach (var project in new[] { seated.Operators, seated.BetaOperators })
        {
            var orders = (await project.RegistryAsync(Ct)).Single(agent => agent.Name == Seated.Orders);

            Assert.True(orders.Frozen, "choosing the freeze did not freeze it in every project");
            Assert.Equal(keep, orders.KeepFrozen);
        }

        // And nobody else.
        Assert.False((await seated.Operators.RegistryAsync(Ct)).Single(agent => agent.Name == Seated.Billing).Frozen);
    }

    [Fact]
    public async Task The_description_does_not_open_over_a_project_the_person_moved_to_while_it_was_being_read()
    {
        // The description opens from the work queue, after each project is read. The queue is held here as a slow read holds
        // it, the person goes to the other project meanwhile, and the description then has no agent in view to be about.
        await using var seated = await OnAgentsAsync(withBeta: true);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        seated.Shell.Queued(_ => held.Task);

        // Enter on the entry closes the menu and queues the read behind what is held, so nothing is drawn from the pool yet.
        var offered = Open(seated, Seated.Orders.Value);
        var menu = (ChoiceDialog)seated.Session.Find(Seated.Orders.Value);

        menu.Choices.SelectedItem = offered.ToList().IndexOf(Freeze);
        Assert.Equal(Freeze, menu.Chosen);
        seated.Session.Enter();

        Assert.False(seated.Session.Has(Freeze), "the description opened before its read, so this test holds nothing back");

        await seated.Shell.GoAsync(ViewPlace.Into(Seated.Beta));

        held.SetResult();
        await seated.Shell.Settled;

        Assert.False(seated.Session.Has(Freeze), "the freeze was described over a project the person had moved to");
        Assert.False(await FrozenAsync(seated.Operators));
        Assert.False(await FrozenAsync(seated.BetaOperators));
    }

    [Fact]
    public async Task Unfreezing_is_described_with_its_own_first_choice_which_leaves_it_frozen()
    {
        await using var seated = await OnAgentsAsync(withBeta: true);

        Assert.True((await seated.Operators.FreezeAsync(Seated.Orders, cancellationToken: Ct)).Changed);
        Assert.True((await seated.BetaOperators.FreezeAsync(Seated.Orders, cancellationToken: Ct)).Changed);
        await AgainAsync(seated);

        await ChooseAsync(seated, Seated.Orders.Value, Unfreeze);

        var describing = (ChoiceDialog)seated.Session.Find(Unfreeze);

        // Not "Keep it running": it is not running, so the stop's words would be wrong.
        Assert.Equal([LeaveItFrozen, Unfreeze], Choices(describing));
        Assert.Contains($"This reaches {Walking.Alpha.Value}, {Seated.Beta.Value}.", describing.Described, StringComparison.Ordinal);
        Assert.Contains(LeaveItFrozen, seated.Session.Text(), StringComparison.Ordinal);
        Assert.Equal(LeaveItFrozen, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        Assert.True(await FrozenAsync(seated.Operators));
        Assert.True(await FrozenAsync(seated.BetaOperators));
    }

    [Fact]
    public async Task Choosing_the_unfreeze_unfreezes_it_in_every_project()
    {
        await using var seated = await OnAgentsAsync(withBeta: true);

        Assert.True((await seated.Operators.FreezeAsync(Seated.Orders, keepFrozen: true, Ct)).Changed);
        Assert.True((await seated.BetaOperators.FreezeAsync(Seated.Orders, keepFrozen: true, Ct)).Changed);
        await AgainAsync(seated);

        await ChooseAsync(seated, Seated.Orders.Value, Unfreeze);

        var describing = (ChoiceDialog)seated.Session.Find(Unfreeze);

        seated.Session.Row(describing.Choices, Unfreeze);
        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        Assert.False(await FrozenAsync(seated.Operators));
        Assert.False(await FrozenAsync(seated.BetaOperators));
    }

    /// <summary>Whether a project's registry says the fixture's first agent is frozen.</summary>
    private static async Task<bool> FrozenAsync(Parley.Client.BrokerClient project) =>
        (await project.RegistryAsync(Ct)).Single(agent => agent.Name == Seated.Orders).Frozen;

    /// <summary>The seated fixture, on the project view with the agents pane focused.</summary>
    private static async Task<Seated> OnAgentsAsync(bool withBeta = false)
    {
        var seated = await Seated.OpenAsync(Ct, withBeta: withBeta);

        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        return seated;
    }

    /// <summary>Reads the registry again after something changed it behind the surface, and returns to the agents pane.</summary>
    private static async Task AgainAsync(Seated seated)
    {
        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();
    }

    /// <summary>Puts the cursor on a row, presses Enter, and reads what the menu offers.</summary>
    private static IReadOnlyList<string> Open(Seated seated, string row)
    {
        seated.Session.Row(seated.Shell.Agents.List, row);
        seated.Session.Enter();

        return Choices((ChoiceDialog)seated.Session.Find(row));
    }

    /// <summary>Opens a row's menu and presses Enter on one entry, waiting for what that queues before anything is drawn.</summary>
    private static async Task ChooseAsync(Seated seated, string row, string entry)
    {
        var offered = Open(seated, row);
        var menu = (ChoiceDialog)seated.Session.Find(row);

        // By its whole label and never by what it contains: the kept freeze's label starts with the plain one's.
        menu.Choices.SelectedItem = offered.ToList().IndexOf(entry);
        Assert.Equal(entry, menu.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);
    }

    private static IReadOnlyList<string> Choices(ChoiceDialog dialog) =>
        [.. dialog.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty)];
}
