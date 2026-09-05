using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That every operation the shared client offers is bound by a surface, and that a surface which
/// binds one the other does not is a decision somebody wrote down.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.7. <c>Parley.Client</c> exists because two surfaces that
/// implemented seating and transcripts separately would disagree about who is in a conversation,
/// which this repository shipped twice in one week. Extracting it removed the duplication; it did
/// not remove the way the two can drift, which is one surface quietly gaining a capability the
/// other lacks and nobody noticing until somebody moves between them.
/// </para>
/// <para>
/// So the check is two-sided. An operation neither surface calls is a method kept alive by its own
/// test, and an operation only one calls has to be named in <c>contracts/operations.md</c> as
/// deliberately absent — which is exactly the argument that file asks for before an operation is
/// admitted, applied in the other direction.
/// </para>
/// </remarks>
public sealed class DriftTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "specs")))
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

    [Fact]
    public void Every_operation_the_client_offers_is_bound_by_at_least_one_surface()
    {
        // A method neither surface calls is one kept alive by its own test, which is how a library
        // grows a shape nothing uses and everybody maintains.
        var console = Text("src", "Parley.Web");
        var terminal = Text("src", "Parley.Cli");

        var orphaned = Operations()
            .Where(name => !console.Contains(name, StringComparison.Ordinal)
                        && !terminal.Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "No surface binds: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void An_operation_only_one_surface_binds_is_named_as_deliberately_absent()
    {
        var console = Text("src", "Parley.Web");
        var terminal = Text("src", "Parley.Cli", "Top");
        var contract = File.ReadAllText(
            Path.Combine(Root(), "specs", "005-tabbed-terminal-agents", "contracts", "operations.md"));

        var undeclared = Operations()
            .Where(name => console.Contains(name, StringComparison.Ordinal)
                        != terminal.Contains(name, StringComparison.Ordinal))
            .Where(name => !contract.Contains(
                name.Replace("Async", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal))
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "One surface binds these and the other does not, and the contract says nothing: "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void The_three_the_contract_excludes_are_still_excluded_from_the_terminal()
    {
        // Asserted from the terminal's own source rather than from the contract, so the pair is a
        // real comparison rather than a document agreeing with itself.
        var terminal = Text("src", "Parley.Cli", "Top");

        foreach (var excluded in new[] { "ForceConcludeAsync", "InstructAsync", "PruneAsync" })
        {
            Assert.DoesNotContain(excluded, terminal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, in both directions: the client really has operations, and both trees really
        // have source in them.
        Assert.NotEmpty(Operations());
        Assert.Contains("RetireAsync", Operations());
        Assert.Contains("BrokerClient", Text("src", "Parley.Web"), StringComparison.Ordinal);
        Assert.Contains("BrokerClient", Text("src", "Parley.Cli", "Top"), StringComparison.Ordinal);
    }
}
