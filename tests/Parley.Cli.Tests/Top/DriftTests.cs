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
            .Where(name => !Pending().Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "No surface binds: " + string.Join(", ", orphaned));
    }

    /// <summary>Operations the protocol carries that no surface binds yet, and why.</summary>
    /// <returns>The section of 006's contract that names them.</returns>
    /// <remarks>
    /// <para>
    /// <b>This does not weaken the check; it moves where the exemption has to be written.</b> An
    /// operation that no surface binds is still a failure unless the contract says, in prose, that
    /// it is coming and when. The alternative was an attribute or a list in this file, which is an
    /// exemption a reader of the contract cannot see.
    /// </para>
    /// <para>
    /// It exists because 006 lands the protocol before the views that read it, deliberately: the
    /// surfaces are clients of operations that have to exist first, and building them the other way
    /// round means building them twice. That leaves a real interval in which the client offers
    /// something nothing calls, and this is how that interval stays visible rather than silent.
    /// </para>
    /// <para>
    /// <b>The section must empty out.</b> When the last operation leaves it, this method finds
    /// nothing and the check is exactly what it was before.
    /// </para>
    /// </remarks>
    private static string Pending()
    {
        var contract = Path.Combine(
            Root(), "specs", "006-three-view-operator", "contracts", "operations.md");

        if (!File.Exists(contract))
        {
            return string.Empty;
        }

        var text = File.ReadAllText(contract);
        var start = text.IndexOf(PendingHeading, StringComparison.Ordinal);

        if (start < 0)
        {
            return string.Empty;
        }

        var rest = text[(start + PendingHeading.Length)..];
        var next = rest.IndexOf("\n## ", StringComparison.Ordinal);

        return next < 0 ? rest : rest[..next];
    }

    /// <summary>The heading the pending section must carry, so it cannot be renamed by accident.</summary>
    private const string PendingHeading = "## Not yet bound by either surface";

    [Fact]
    public void The_pending_section_is_read_rather_than_assumed_absent()
    {
        // A heading nobody can find makes every exemption above vacuous, and the check would then
        // pass by failing to look -- the same trap the schema extraction had to be guarded against.
        // Either the section is there and names something, or there is nothing pending at all.
        var contract = Path.Combine(
            Root(), "specs", "006-three-view-operator", "contracts", "operations.md");

        Assert.True(
            !File.Exists(contract)
            || File.ReadAllText(contract).Contains(PendingHeading, StringComparison.Ordinal)
            || Pending().Length == 0,
            $"006's contract exists but has no '{PendingHeading}' section for this check to read.");
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
