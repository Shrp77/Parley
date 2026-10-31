using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That the operator contract and the document stay the same list.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2 opens by warning that an adapter surfacing an operator operation into an agent's
/// tool set would breach §10.1, and offers a naming convention as the protection —
/// <c>force_conclude</c> deliberately not <c>conclude</c>. The document says plainly that this is
/// thin: it depends on whoever writes the adapter reading carefully.
/// </para>
/// <para>
/// There is no agent-side contract in this assembly any more: an agent receives what a person says as a
/// Request, through §10.1, and nothing else. What is left to guard is that the operator contract declares
/// every operation §10.2 names and nothing it does not.
/// </para>
/// </remarks>
[Integration]
public sealed class ContractSeparationTests
{
    /// <summary>The operations SPEC.md §10.2 names, read from the document itself.</summary>
    /// <remarks>
    /// <para>
    /// Read rather than listed. A hand-kept copy of §10.2 in a test file is a second declaration
    /// of the same thing, and the failure it permits is silent in the direction that matters: an
    /// operation implemented, bound to the wire, rendered on a page, and never written into the
    /// document — which is exactly what happened to <c>waiting</c>. Every check passed, because
    /// every check was reading the copy.
    /// </para>
    /// <para>
    /// Line endings normalised on load. SPEC.md is CRLF on Windows and LF on the Linux runner,
    /// and .NET anchors <c>$</c> before a bare line feed only — a pattern anchored to end-of-line
    /// otherwise matches everything on two runners and nothing on the third.
    /// </para>
    /// </remarks>
    private static readonly string[] OperatorOnly = OperationsInSection102();

    /// <summary>Extracts the operation names from §10.2's table.</summary>
    /// <returns>Each operation, in the casing the interface uses.</returns>
    private static string[] OperationsInSection102()
    {
        var document = File.ReadAllText(RepositoryPath("SPEC.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = document.IndexOf("### 10.2", StringComparison.Ordinal);
        Assert.True(start >= 0, "SPEC.md has no §10.2, so this check would pass by vacuity.");

        var end = document.IndexOf("\n## 11.", start, StringComparison.Ordinal);
        Assert.True(end > start, "§10.2 does not end, so the extraction is reading the whole file.");

        var rows = Regex.Matches(
            document[start..end], @"^\| `([a-z_]+)\(", RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        Assert.True(
            rows.Count > 5,
            "§10.2's operation table did not parse, which would make every check below pass "
            + "without testing anything.");

        return [.. rows.Select(m => Camel(m.Groups[1].Value))];
    }

    /// <summary>Turns a documented operation name into the method name that implements it.</summary>
    /// <param name="documented">The name as §10.2 writes it, in snake case.</param>
    /// <returns>The name in the casing the interface uses, without the Async suffix.</returns>
    private static string Camel(string documented) =>
        string.Concat(documented
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }

    [Fact]
    public void The_operator_contract_declares_every_operation_the_document_does()
    {
        // The interface is the capability list, so it is worth one check that it is the whole
        // list. §10.2 is what a V1 broker implements; an interface missing an operation would be
        // a client quietly unable to ask for something the document promises.
        var declared = typeof(IParleyBrokerClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = OperatorOnly
            .Concat(["Implements"])
            .Select(op => op + "Async")
            .Where(name => !declared.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "broker V1 declares operations this interface does not: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_operator_contract_declares_nothing_the_document_does_not()
    {
        // The direction that was missing, and the one that let `waiting` exist as an
        // implementation with no specification: declared on the interface, bound to the wire,
        // rendered on a page, and absent from §10.2 — so a second implementer reading the
        // document would have built a broker this console could not use, and neither side would
        // have been wrong about anything they could see.
        //
        // SPEC.md is the primary artifact and the code is a reference implementation of it. An
        // operation that exists only here is the implementation quietly becoming the spec.
        var documented = OperatorOnly
            .Concat(["Implements"])
            .Select(op => op + "Async")
            .ToHashSet(StringComparer.Ordinal);

        var undocumented = typeof(IParleyBrokerClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(name => !documented.Contains(name))
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            "this interface declares operations SPEC.md §10.2 does not: "
            + string.Join(", ", undocumented));
    }
}
