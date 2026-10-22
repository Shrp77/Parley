using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That the operator service on the wire and SPEC.md §10.2 name the same operations.
/// </summary>
/// <remarks>
/// <para>
/// <b>This check did not exist, and the gap it leaves is one-sided.</b> The document is checked
/// against the C# contract — <c>ContractSeparationTests</c> fails when
/// <c>IParleyBrokerClient</c> declares an operation §10.2 does not, and it caught this feature's
/// readback before the document had it. Nothing checked the *wire*.
/// </para>
/// <para>
/// The wire is where the gap matters most. <c>proto/parley.proto</c> opens by saying SPEC.md is the
/// authority and it is not — "a message defined here and absent there is a defect here" — and that
/// is a promise nothing was holding it to. A service that grew an rpc the document never described
/// would be a broker implementing something no client could rely on, and a document describing an
/// operation the wire lacks would be a V1 contract nobody satisfies.
/// </para>
/// <para>
/// Names are compared after translating one convention into the other: the document writes
/// <c>force_conclude(conversation)</c> and the wire writes <c>ForceConclude</c>. The translation is
/// the interesting part rather than an inconvenience — §10.2's protection against an operator
/// operation reaching an agent's tool set is partly that the names differ from §10.1's, so the
/// mapping between the two spellings has to be exact for that protection to mean anything.
/// </para>
/// </remarks>
public sealed class WireMatchesTheDocumentTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>Every operation named in §10.2's table, in the document's own spelling.</summary>
    /// <returns>The names.</returns>
    private static IReadOnlyCollection<string> Documented()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "SPEC.md")).ReplaceLineEndings("\n");

        var section = text.IndexOf("### 10.2 Operator operations", StringComparison.Ordinal);

        Assert.True(section > 0, "§10.2 is not where this expects it");

        var next = text.IndexOf("\n## ", section, StringComparison.Ordinal);
        var body = next < 0 ? text[section..] : text[section..next];

        // Rows read `| `name(args)` | description |`. The backticked head of the row is the name.
        var names = Regex
            .Matches(body, @"^\| `([a-z_]+)\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // A regex that silently matched nothing would make every comparison below pass by vacuity,
        // which is the trap the spec suite already records having fallen into once.
        Assert.True(names.Count > 10, $"only {names.Count} operations found in §10.2");

        return names;
    }

    /// <summary>Every rpc on the operator service, translated into the document's spelling.</summary>
    /// <returns>The names.</returns>
    private static IReadOnlyCollection<string> OnTheWire() =>
        typeof(Parley.Transport.Operators.OperatorsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Select(name => name.EndsWith("Async", StringComparison.Ordinal)
                ? name[..^"Async".Length]
                : name)
            .Distinct(StringComparer.Ordinal)
            .Select(Underscored)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Turns <c>ForceConclude</c> into <c>force_conclude</c>.</summary>
    /// <param name="name">The wire's spelling.</param>
    /// <returns>The document's.</returns>
    private static string Underscored(string name) =>
        Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    /// <summary>
    /// Wire names the document deliberately spells differently, or does not name as an operation.
    /// </summary>
    /// <remarks>
    /// Every entry is a decision rather than an oversight, and each is written out so that adding
    /// one is visible. A list that grew silently would turn this check into a formality.
    /// </remarks>
    private static readonly Dictionary<string, string> Spelled = new(StringComparer.Ordinal)
    {
        // The document names these by what they report, not by the verb a client calls.
        ["describe_registry"] = "registry",
        ["describe_refusals"] = "refusals",
        ["describe_clarifications"] = "clarifications",
        ["describe_results"] = "results",
        ["describe_flags"] = "flags",
        ["describe_waiting"] = "waiting",
        ["list_conversations"] = "conversations",
    };

    [Fact]
    public void Every_operator_rpc_is_an_operation_the_document_describes()
    {
        var documented = Documented();

        var undocumented = OnTheWire()
            .Select(name => Spelled.TryGetValue(name, out var spelled) ? spelled : name)
            .Where(name => !documented.Contains(name))
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            "the wire carries operator operations §10.2 does not describe: "
            + string.Join(", ", undocumented));
    }

    [Fact]
    public void Every_operation_the_document_describes_is_on_the_wire()
    {
        var onWire = OnTheWire()
            .Select(name => Spelled.TryGetValue(name, out var spelled) ? spelled : name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = Documented().Where(name => !onWire.Contains(name)).ToList();

        Assert.True(
            missing.Count == 0,
            "§10.2 describes operations the wire does not carry, so no broker can satisfy V1: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void The_operations_that_were_removed_are_gone_from_both_and_a_present_one_is_in_both()
    {
        // Non-vacuity. Both checks above would pass on a wire and a document that had each quietly lost the
        // same operation, so one that must be there is asserted, and the two that were removed are asserted gone
        // from both rather than left to pass by being absent from one.
        var onWire = OnTheWire().Select(n => Spelled.TryGetValue(n, out var s) ? s : n).ToList();

        Assert.Contains("export", Documented());
        Assert.Contains("export", onWire);

        Assert.DoesNotContain("instruct", Documented());
        Assert.DoesNotContain("instructions", Documented());
        Assert.DoesNotContain("instruct", onWire);
        Assert.DoesNotContain("instructions", onWire);
    }
}
