using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// A project's settings are a view of their own, reached from the Project menu, and each says what it decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the owner's first look at 0.35.0.</b> Auto Accept was a bare entry on the Project menu, beside
/// creating and removing projects, and the owner asked "Auto-Accept what??" and for a configuration view with a menu
/// item that leads there. These hold both halves: the menu leads there from inside a project, and the view explains
/// the setting before anybody changes it.
/// </para>
/// <para>
/// Run with no broker listening, which is what makes the arrival's read observable: a view that did not read on arrival
/// would still say it was reading, and one that treated a dead broker as no settings would show an empty list.
/// </para>
/// </remarks>
public sealed class ConfigurationViewTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task The_project_menu_leads_to_the_configuration_from_inside_a_project()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        // The agents pane has focus, whose own menu is Agent; the way to the project's settings is the Project menu's.
        var menus = MenuRow.Menus([.. shell.Actions(), .. shell.Global()]);
        var project = Assert.Single(menus, menu => menu.Title == "Project");

        Assert.Contains(
            project.Items, item => item.Label.Contains(Shell.ConfigurationTitle, StringComparison.Ordinal));

        driving.Press(Shell.ConfigurationKey);
        await shell.Settled;

        Assert.Equal(Viewing.Configuration, shell.Place.Viewing);
        Assert.True(shell.Configuration.Visible);
    }

    [Fact]
    public async Task Auto_Accept_is_no_longer_a_bare_entry_on_the_projects_pane()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        Assert.DoesNotContain(shell.Actions(), action => action.Label == AutoAccepting.Title);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_read_is_said_rather_than_shown_as_no_settings()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.GoAsync(ViewPlace.Configure(Walking.Alpha));
        await shell.Settled;

        Assert.Empty(shell.Configuration.Settings);
        Assert.Contains("could not be read", shell.Configuration.Meaning, StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Actions(), action => action.Label == ConfigurationRegion.ChangeTitle);
    }

    [Fact]
    public async Task A_setting_says_what_it_decides_and_can_be_changed_from_its_row()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.GoAsync(ViewPlace.Configure(Walking.Alpha));
        await shell.Settled;

        shell.Configuration.Show([AutoAccepting.AsSetting(on: true)]);

        var row = Assert.Single(shell.Configuration.List.Source!.ToList().Cast<string>());

        Assert.Contains(AutoAccepting.Title, row, StringComparison.Ordinal);
        Assert.EndsWith("on", row, StringComparison.Ordinal);

        // The meaning opens by saying what is being accepted, which is the question the owner asked.
        Assert.StartsWith("Parley asks every agent", shell.Configuration.Meaning, StringComparison.Ordinal);
        Assert.Contains(shell.Actions(), action => action.Label == ConfigurationRegion.ChangeTitle);
    }

    [Fact]
    public void The_view_and_the_dialog_that_changes_it_describe_the_same_switch()
    {
        Assert.Contains(AutoAccepting.Meaning, AutoAccepting.Described("shop", on: true), StringComparison.Ordinal);
        Assert.Equal(AutoAccepting.Meaning, AutoAccepting.AsSetting(on: false).Meaning);
    }

    [Fact]
    public void Back_from_the_configuration_is_the_project_it_belongs_to()
    {
        var place = ViewPlace.Configure(Walking.Alpha);

        Assert.Equal(ViewPlace.Into(Walking.Alpha), place.Back());
        Assert.Contains(Walking.Alpha.Value, place.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_ui_opens_on_the_page_that_carries_the_same_setting()
    {
        var address = Deep.For("http://localhost:5000", ViewPlace.Configure(Walking.Alpha));

        Assert.Equal($"http://localhost:5000{Deep.Ownership}?project=alpha", address);
    }
}
