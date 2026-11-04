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
