using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Walking in and back out the way a person does, and handing what is on screen to a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test in this suite arrives at a view by setting it.</b> That is how a test
/// reaches the project view without first building a deployment to navigate through, and it is
/// also how a suite comes to pass while the route a person actually takes is broken — which is
/// the shape of the defect that opened this branch, where every key on the surface was dead and
/// every test drove the thing under test directly.
/// </para>
/// <para>
/// So this one presses keys and nothing else.
/// </para>
/// </remarks>
public sealed class NavigatingTests
{
    private static BrokerDirectory Directory() =>
        new(
            [
                new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null),
                new ProjectSettings(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:2", null, null),
            ],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task Enter_opens_the_highlighted_project_and_escape_comes_back_to_it()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Equal(Viewing.Projects, shell.Place.Viewing);

        var chosen = shell.Projects.Selected?.Name.Value;

        driving.Enter();
        await shell.Settled;

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
        Assert.Equal(chosen, shell.Place.Project?.Value);

        driving.Press(Terminal.Gui.Input.Key.Esc);
        await shell.Settled;

        // Back keeps the place rather than the level: the same project is still highlighted, so
        // looking at two projects in turn costs two keystrokes rather than two descents.
        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
        Assert.Equal(chosen, shell.Projects.Selected?.Name.Value);
    }

    [Fact]
    public async Task Escape_at_the_top_does_nothing_rather_than_anything_surprising()
    {
        // A person pressing it twice has not done anything wrong, and a surface that quit on the
        // second press would lose their work for a keystroke that meant "go back".
        //
        // **This test was named for that property and did not check it, and the surface did quit.**
        // It asserted on the view, which is still Projects on a shell that has just requested a
        // stop — so it passed while Terminal.Gui's application-scope `Esc => Quit` binding, which
        // the surface used to fall through to at the top of the tree, ended the program. The
        // assertion that would have caught it is the last one.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Press(Terminal.Gui.Input.Key.Esc);
        driving.Press(Terminal.Gui.Input.Key.Esc);
        await shell.Settled;

        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
        Assert.False(shell.Place.CanGoBack);
        Assert.False(
            ((Terminal.Gui.App.IRunnable)shell).StopRequested,
            "Escape at the top of the tree asked the application to stop");
    }

    [Fact]
    public async Task Going_back_from_a_conversation_lands_on_its_project()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        await shell.BackAsync();
        await shell.Settled;

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
        Assert.Equal("alpha", shell.Place.Project?.Value);

        await shell.BackAsync();
        await shell.Settled;

        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
    }

    [Fact]
    public async Task A_second_project_is_opened_by_arrowing_to_it_and_pressing_enter()
    {
        // Two projects, so this cannot pass by opening whichever one happened to be first.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Row(shell.Projects.List, "beta");
        driving.Enter();
        await shell.Settled;

        Assert.Equal("beta", shell.Place.Project?.Value);
    }

    [Fact]
    public async Task The_browser_key_is_bound_and_reported_rather_than_silent()
    {
        // F8 is exercised through the key rather than through the action, so a binding that was
        // removed from OnKeyDown would fail here even while the action list still offered it.
        //
        // The launcher is the harness's, which records an address and opens nothing: this pressed
        // F8 against a real Process.Start and put a tab in the developer's browser on every run of
        // the suite. Driving.Show carries that.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Press(Shell.BrowserKey);
        await shell.Settled;

        // Asked for, said out loud, and the surface still running -- which is the property, since
        // a headless machine is an ordinary place to run this and FR-014 requires the surface to
        // survive being unable to launch a browser at all.
        Assert.Single(driving.Opened);
        Assert.NotEmpty(shell.Said);
        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
    }

    [Fact]
    public async Task The_browser_key_names_the_view_it_was_pressed_on()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        driving.Press(Shell.BrowserKey);
        await shell.Settled;

        // The address itself, not only the sentence about it. Reading the message alone would pass
        // against a surface that named the right conversation and handed the browser something
        // else, which is exactly the shape of the defect ConsoleAddressTests was written for.
        Assert.Contains(
            "some-discussion",
            Assert.Single(driving.Opened),
            StringComparison.Ordinal);
        Assert.Contains("some-discussion", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_browser_key_is_listed_where_a_person_can_find_it()
    {
        // No action exists only as a key, and a key nobody is told about is one nobody presses.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Contains(shell.Actions(), action => action.Label.Contains("browser", StringComparison.Ordinal));
    }
}
