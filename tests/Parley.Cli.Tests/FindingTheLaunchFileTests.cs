using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>How a launch finds its configuration, when the file is no longer told where it is (SPEC.md §4.1.2).</summary>
[Integration]
public sealed class FindingTheLaunchFileTests : IDisposable
{
    private const string File =
        """
        { "mcpServers": { "parley-acme": { "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme" } } },
          "parleyMachines": { "laptop": { "parley-acme": { "PARLEY_OWNS": "Answers." } } } }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("parley-find-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Checkout(string name, string text = File)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        System.IO.File.WriteAllText(Path.Combine(folder, ".mcp.json"), text);

        return folder;
    }

    [Fact]
    public void The_folder_a_launch_started_in()
    {
        var here = Checkout("infra");

        Assert.Equal(here, LocatedLaunch.Find(null, "infra", "acme", here)?.Folder);
    }

    [Fact]
    public void A_parent_when_it_started_in_a_subfolder()
    {
        var here = Checkout("infra");
        var below = Directory.CreateDirectory(Path.Combine(here, "src", "deep")).FullName;

        Assert.Equal(here, LocatedLaunch.Find(null, "infra", "acme", below)?.Folder);
    }

    [Fact]
    public void Never_past_the_repository()
    {
        // A parent's configuration is another repository's, however well it fits.
        System.IO.File.WriteAllText(Path.Combine(_root, ".mcp.json"), File);

        var inner = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Null(LocatedLaunch.Find(null, "infra", "acme", inner));
    }

    [Fact]
    public void A_parent_outside_any_repository_is_not_this_launchs()
    {
        // `/tmp/.mcp.json` naming this role must not choose this agent's broker because somebody left it there.
        System.IO.File.WriteAllText(Path.Combine(_root, ".mcp.json"), File);

        var below = Directory.CreateDirectory(Path.Combine(_root, "scratch", "work")).FullName;

        Assert.Null(LocatedLaunch.Find(null, "infra", "acme", below));
    }

    [Fact]
    public void The_folder_it_started_in_counts_with_no_repository_around_it()
    {
        var here = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;

        System.IO.File.WriteAllText(Path.Combine(here, ".mcp.json"), File);

        Assert.Equal(here, LocatedLaunch.Find(null, "infra", "acme", here)?.Folder);
    }

    [Fact]
    public void An_old_path_from_another_machine_is_passed_over()
    {
        var here = Checkout("infra");

        Assert.Equal(
            here,
            LocatedLaunch.Find(Path.Combine(_root, "not-on-this-machine"), "infra", "acme", here)?.Folder);
    }

    [Fact]
    public void A_folder_the_person_named_comes_first()
    {
        var named = Checkout("named");
        var here = Checkout("infra");

        Assert.Equal(named, LocatedLaunch.Find(named, "infra", "acme", here)?.Folder);
    }

    [Fact]
    public void A_named_folder_with_no_entry_for_this_launch_is_passed_over()
    {
        var clone = Checkout("orders", """{ "mcpServers": { "parley-acme": { "env": { "PARLEY_AGENT": "orders" } } } }""");
        var here = Checkout("infra");

        Assert.Equal(here, LocatedLaunch.Find(clone, "infra", "acme", here)?.Folder);
    }

    [Fact]
    public void A_file_with_no_entry_for_this_launch_is_not_this_launchs()
    {
        var here = Checkout("infra");

        Assert.Null(LocatedLaunch.Find(null, "orders", "acme", here));
    }

    [Fact]
    public void A_broken_section_is_reported_whichever_machine_it_concerns()
    {
        var here = Checkout(
            "infra",
            """
            { "mcpServers": { "parley-acme": { "env": { "PARLEY_AGENT": "infra" } } },
              "parleyMachines": { "server": { "parley-acme": { "PARLEY_AGENT": "other" } } } }
            """);

        var located = LocatedLaunch.Find(null, "infra", "acme", here);

        Assert.Contains("PARLEY_AGENT", located?.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_named_twice_in_a_section_is_a_problem_and_not_silence()
    {
        var here = Checkout(
            "infra",
            """
            { "mcpServers": { "parley-acme": { "env": { "PARLEY_AGENT": "infra" } } },
              "parleyMachines": { "laptop": { "parley-acme": { "PARLEY_OWNS": "One.", "PARLEY_OWNS": "Two." } } } }
            """);

        Assert.NotNull(LocatedLaunch.Find(null, "infra", "acme", here)?.Problem);
    }

    [Fact]
    public void A_launch_naming_no_project_is_the_entry_naming_none()
    {
        var here = Checkout(
            "infra",
            """
            { "mcpServers": {
                "parley": { "env": { "PARLEY_AGENT": "infra" } },
                "parley-acme": { "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme" } } },
              "parleyMachines": { "laptop": { "parley": { "PARLEY_OWNS": "Unpinned." } } } }
            """);

        Assert.Equal("parley", LocatedLaunch.Find(null, "infra", null, here)?.Membership?.Key);
    }

    [Fact]
    public void Several_entries_that_could_be_this_launch_are_a_problem_and_not_silence()
    {
        var here = Checkout(
            "infra",
            """
            { "mcpServers": {
                "parley-acme": { "env": { "PARLEY_AGENT": "infra" } },
                "parley-beta": { "env": { "PARLEY_AGENT": "infra" } } },
              "parleyMachines": { "laptop": { "parley-acme": { } } } }
            """);

        Assert.Contains("PARLEY_PROJECT", LocatedLaunch.Find(null, "infra", null, here)?.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_waits_for_a_writer_holding_the_file_rather_than_reading_it_as_silence()
    {
        var here = Checkout("infra");
        var path = Path.Combine(here, ".mcp.json");
        var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            await held.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        var located = LocatedLaunch.Find(null, "infra", "acme", here);

        Assert.Equal("Answers.", located?.Configuration.BlockFor("laptop", "parley-acme")?.Owns);
    }

    [Fact]
    public void A_file_held_past_the_wait_is_a_problem_and_not_silence()
    {
        var here = Checkout("infra");

        using var held = new FileStream(Path.Combine(here, ".mcp.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.NotNull(LocatedLaunch.Find(null, "infra", "acme", here)?.Problem);
    }

    [Fact]
    public void Reading_writes_nothing()
    {
        var here = Checkout("infra");
        var path = Path.Combine(here, ".mcp.json");
        var before = System.IO.File.GetLastWriteTimeUtc(path);

        _ = LocatedLaunch.Find(null, "infra", "acme", here);

        Assert.Equal(File, System.IO.File.ReadAllText(path));
        Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(path));
    }
}
