using Xunit;

namespace Parley.Terminal.Tests;

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
/// <para>
/// <b>Since 0.26.0 that excuse no longer passes the file.</b> The owner decided every surface offers every operator
/// operation, and <c>Every_operation_is_bound_by_both_surfaces</c> asserts it with no exemption, so an operation named
/// in the contract as absent from one surface still fails there. The contract check stays because it names what the
/// document says, which is the better message while somebody is deciding.
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
        // **There are three surfaces, and this test named two of them until the terminal moved.**
        // It read `src/Parley.Cli` as "the terminal", which was the whole tool -- so an operation
        // bound only by a one-shot command counted as bound by a screen. `PruneAsync` was exactly
        // that until 0.26.0: `parley prune` bound it and neither screen did, deliberately. The check
        // passed for a reason that had stopped being true, and the honest repair was to name the
        // command line as the third surface rather than to exempt the operation. Both screens bind
        // it now, and `Every_operation_is_bound_by_both_surfaces` holds them to that.
        var console = Text("src", "Parley.Web");
        var terminal = Text("src", "Parley.Terminal");
        var commands = Text("src", "Parley.Cli");

        var orphaned = Operations()
            .Where(name => !Calls(console, name)
                        && !Calls(terminal, name)
                        && !Calls(commands, name))
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
        var terminal = Text("src", "Parley.Terminal");
        var contract = File.ReadAllText(
            Path.Combine(Root(), "specs", "005-tabbed-terminal-agents", "contracts", "operations.md"));

        var undeclared = Operations()
            .Where(name => Calls(console, name)
                        != Calls(terminal, name))
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
    public void Every_operation_is_bound_by_both_surfaces()
    {
        // **The owner's parity decision, asserted without exclusions.** The check above lets the contract
        // excuse an operation one surface binds; this one excuses nothing. Asserted from the two trees'
        // own source rather than from the contract, so it is a real comparison rather than a document
        // agreeing with itself.
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
