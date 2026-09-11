using Terminal.Gui.Input;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That a repository is found by walking to it rather than by typing its path.
/// </summary>
/// <remarks>
/// <b>Asked for by the owner, who was typing an absolute path per agent.</b> Nineteen repositories
/// is nineteen chances to mistype one, into a field that could only say afterwards that what was
/// typed was not a directory. What this adds is the walk; what it does not take away is the field,
/// which is still typed into and pasted into.
/// </remarks>
public sealed class BrowsingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-browsing-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test's temporary directory that will not go is not a failure of the thing tested.
        }
    }

    /// <summary>Makes a folder under the root, optionally a repository.</summary>
    private string Folder(string name, bool repository = false)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);

        if (repository)
        {
            Directory.CreateDirectory(Path.Combine(path, ".git"));
        }

        return path;
    }

    [Fact]
    public void It_lists_what_is_in_the_folder_and_says_which_are_repositories()
    {
        _ = Folder("orders", repository: true);
        _ = Folder("notes");

        using var driving = Driving.Open();

        var browsing = new Browsing(_root);

        driving.Show(browsing);

        var rows = Drawn.Rows(browsing.List);

        Assert.Contains(rows, row => row.StartsWith("orders", StringComparison.Ordinal)
            && row.Contains(Browsing.Repository, StringComparison.Ordinal));

        // The one thing a general file browser cannot say, and the whole question somebody adding
        // an agent is asking.
        Assert.Contains(rows, row => row.StartsWith("notes", StringComparison.Ordinal)
            && !row.Contains(Browsing.Repository, StringComparison.Ordinal));
    }

    [Fact]
    public void A_dot_folder_is_left_out()
    {
        _ = Folder(".hidden");
        _ = Folder("orders", repository: true);

        using var driving = Driving.Open();

        var browsing = new Browsing(_root);

        driving.Show(browsing);

        // `.git` is the clearest case: offering to walk into the thing that makes the folder above
        // it a repository is offering a wrong answer.
        Assert.DoesNotContain(Drawn.Rows(browsing.List), row => row.Contains(".hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_does_not_offer_to_be_added()
    {
        using var driving = Driving.Open();

        var browsing = new Browsing(Folder("notes"));

        driving.Show(browsing);

        Assert.DoesNotContain(Drawn.Rows(browsing.List), row => row == Browsing.Use);
    }

    [Fact]
    public void A_repository_offers_to_be_added()
    {
        using var driving = Driving.Open();

        var browsing = new Browsing(Folder("orders", repository: true));

        driving.Show(browsing);

        Assert.Contains(Drawn.Rows(browsing.List), row => row == Browsing.Use);
    }

    [Fact]
    public void Walking_into_a_folder_shows_what_is_in_it()
    {
        var orders = Folder("orders", repository: true);

        _ = Directory.CreateDirectory(Path.Combine(orders, "docs"));

        using var driving = Driving.Open();

        var browsing = new Browsing(_root);

        driving.Show(browsing);
        driving.Row(browsing.List, "orders" + Browsing.Repository);
        browsing.List.NewKeyDownEvent(Key.Enter);

        Assert.Equal(orders, browsing.At);
        Assert.Contains(Drawn.Rows(browsing.List), row => row.StartsWith("docs", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_the_folder_that_is_open_answers_with_it()
    {
        var orders = Folder("orders", repository: true);

        using var driving = Driving.Open();

        var browsing = new Browsing(orders);
        string? chosen = null;
        var answered = false;

        browsing.Done += (_, folder) => { chosen = folder; answered = true; };

        driving.Show(browsing);
        driving.Row(browsing.List, Browsing.Use);
        browsing.List.NewKeyDownEvent(Key.Enter);

        Assert.True(answered);
        Assert.Equal(orders, chosen);
    }

    [Fact]
    public void Going_up_one_reaches_the_folder_above()
    {
        var orders = Folder("orders", repository: true);

        using var driving = Driving.Open();

        var browsing = new Browsing(orders);

        driving.Show(browsing);
        driving.Row(browsing.List, Browsing.Up);
        browsing.List.NewKeyDownEvent(Key.Enter);

        Assert.Equal(_root, browsing.At);
    }

    [Fact]
    public void Escape_chooses_nothing()
    {
        using var driving = Driving.Open();

        var browsing = new Browsing(_root);
        var answered = false;
        string? chosen = "something";

        browsing.Done += (_, folder) => { chosen = folder; answered = true; };

        driving.Show(browsing);
        browsing.NewKeyDownEvent(Key.Esc);

        Assert.True(answered);
        Assert.Null(chosen);
    }

    [Fact]
    public void With_nothing_added_yet_it_starts_at_the_home_directory()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Browsing.Opening(null));
    }

    [Fact]
    public void After_one_is_added_it_starts_beside_it()
    {
        // The shortcut the owner asked for: repositories sit next to each other, so the second one
        // is almost always in the folder the first came out of.
        var orders = Folder("orders", repository: true);

        Assert.Equal(_root, Browsing.Opening(orders));
    }

    [Fact]
    public void A_remembered_folder_that_has_gone_falls_back_rather_than_failing()
    {
        var gone = Path.Combine(_root, "deleted-since");

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Browsing.Opening(gone));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_is_said_rather_than_shown_empty()
    {
        // Walking into a folder that cannot be listed and leaving the person in an empty list is a
        // screen that looks broken. It says so and stays where it was.
        using var driving = Driving.Open();

        var browsing = new Browsing(_root);

        driving.Show(browsing);

        var was = browsing.At;

        browsing.Walk(Path.Combine(_root, "not-here-at-all"));

        Assert.Equal(Browsing.Unreadable, browsing.Said);
        Assert.Equal(was, browsing.At);
    }

    [Fact]
    public void A_worktree_counts_as_a_repository()
    {
        // A worktree holds a `.git` file rather than a directory, and the same test the add form
        // makes has always counted both — a folder this offered and that refused would be a screen
        // arguing with itself.
        var worktree = Folder("detached");

        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        Assert.True(Browsing.IsRepository(worktree));
        Assert.False(Browsing.IsRepository(Folder("plain")));
    }

    [Fact]
    public void The_path_it_is_showing_is_on_the_screen()
    {
        // Without it the list is a set of names with no answer to "where am I", which is the one
        // thing somebody walking a filesystem needs.
        using var driving = Driving.Open();

        var browsing = new Browsing(_root);

        driving.Show(browsing);

        Assert.Contains(_root, driving.Text(), StringComparison.Ordinal);
    }
}
