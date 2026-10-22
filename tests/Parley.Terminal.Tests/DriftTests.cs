using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every operation the shared client offers is bound by both screens, and by at least one surface.
/// </summary>
/// <remarks>
/// <c>Parley.Client</c> is shared so that the surfaces cannot disagree about seating and transcripts. It does not stop
/// one surface gaining a capability the other lacks, which this check does: every surface offers every operator
/// operation, and an operation no surface calls is a method kept alive by its own test.
/// </remarks>
public sealed class DriftTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static IEnumerable<string> Operations() =>
        typeof(Parley.Client.BrokerClient)
            .GetMethods(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name.EndsWith("Async", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static string Text(params string[] directory)
    {
        var at = Path.Combine([Root(), .. directory]);

        return string.Join(
            "\n",
            Directory.EnumerateFiles(at, "*.cs*", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                .Select(File.ReadAllText));
    }

    /// <summary>Whether source calls an operation, rather than merely containing its name.</summary>
    /// <param name="source">A surface's source, from <see cref="Text"/>.</param>
    /// <param name="name">The operation's method name.</param>
    /// <returns>Whether it is called outside a comment, on something other than the web host.</returns>
    /// <remarks>
    /// <b>A substring was not a binding, and the combined review gate found what that cost.</b> The web UI started no
    /// agent anywhere — and this passed, because <c>ConsoleHost</c> calls <c>app.StartAsync(</c>. The same check was
    /// satisfied by an operation's name in a comment. A call is a dot, the name and a bracket, in a line that is not a
    /// comment; the host's own start is excluded by name, which is the one collision there is.
    /// </remarks>
    private static bool Calls(string source, string name) =>
        source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && !line.TrimStart().StartsWith("@*", StringComparison.Ordinal))
            .Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, $@"(?<!\bapp)\.{System.Text.RegularExpressions.Regex.Escape(name)}\("));

    [Fact]
    public void Every_operation_the_client_offers_is_bound_by_at_least_one_surface()
    {
        // A method no surface calls is one kept alive by its own test, which is how a library grows
        // a shape nothing uses and everybody maintains.
        //
        // Three surfaces: the web console, the terminal and the command line.
        var console = Text("src", "Parley.Web");
        var terminal = Text("src", "Parley.Terminal");
        var commands = Text("src", "Parley.Cli");

        var orphaned = Operations()
            .Where(name => !Calls(console, name)
                        && !Calls(terminal, name)
                        && !Calls(commands, name))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "No surface binds: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void Every_operation_is_bound_by_both_surfaces()
    {
        // Parity, asserted without exclusions from the two trees' own source.
        var console = Text("src", "Parley.Web");
        var terminal = Text("src", "Parley.Terminal");

        var unbound = Operations()
            .Where(name => !Calls(console, name)
                        || !Calls(terminal, name))
            .ToList();

        Assert.True(
            unbound.Count == 0,
            "Every surface offers every operator operation, and these are missing from one: "
            + string.Join(", ", unbound));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, in both directions: the client really has operations, and both trees really
        // have source in them.
        Assert.NotEmpty(Operations());
        Assert.Contains("RetireAsync", Operations());
        Assert.Contains("BrokerClient", Text("src", "Parley.Web"), StringComparison.Ordinal);
        Assert.Contains("BrokerClient", Text("src", "Parley.Terminal"), StringComparison.Ordinal);
    }
}
