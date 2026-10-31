using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That nothing on this surface is kept alive only by the file it lives in.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.8. Run 3.4 found a region taking a parameter it never used —
/// the first step towards a region that fetched its own data — and that was one instance of a shape
/// this rewrite is unusually exposed to. Everything here was written in one pass against a design
/// that moved while it was being written, so the widened member that nothing outside its own file
/// ever called is the likely residue.
/// </para>
/// <para>
/// <c>internal</c> is the claim that somebody else needs this. A member nothing outside its file
/// names is either dead or should be private, and the difference matters because the next person
/// reading the file cannot tell a deliberate seam from a leftover.
/// </para>
/// </remarks>
[Integration]
public sealed class DeadSurfaceTests
{
    /// <summary>The one exception, and why it is one rather than a hole in the rule.</summary>
    /// <remarks>
    /// A member used by a second type in the same file cannot be private, so the rule below cannot
    /// distinguish it from a leftover by counting files. Listed by name so that adding to the list
    /// is a decision somebody makes rather than a rule quietly getting weaker: `Catalogue` is the
    /// node file `Adding` writes, and `Adding` is the only caller.
    /// </remarks>
    private static readonly string[] SecondTypeInItsOwnFile = ["Catalogue.EnsureAsync"];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>Every source file that could name something, and what it says.</summary>
    private static IReadOnlyList<(string Name, string Text)> Sources()
    {
        var files = new List<(string, string)>();

        foreach (var tree in new[]
                 {
                     Path.Combine(Root(), "src", "Parley.Terminal"),
                     Path.Combine(Root(), "tests", "Parley.Terminal.Tests"),
                 })
        {
            foreach (var file in Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add((Path.GetFileName(file), File.ReadAllText(file)));
            }
        }

        return files;
    }

    /// <summary>Every internal member declared by a type on this surface, with its file.</summary>
    private static IEnumerable<(string Type, string Member)> Declared()
    {
        foreach (var type in typeof(Shell).Assembly.GetTypes()
                     .Where(t => t.Namespace == "Parley.Terminal")
                     .Where(t => !t.Name.Contains('<', StringComparison.Ordinal)))
        {
            const BindingFlags Where = BindingFlags.NonPublic | BindingFlags.Public
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.DeclaredOnly;

            foreach (var member in type.GetMembers(Where))
            {
                var internalToThis = member switch
                {
                    MethodInfo method => method.IsAssembly && !method.IsSpecialName,
                    FieldInfo field => field.IsAssembly,
                    PropertyInfo property => property.GetMethod?.IsAssembly == true,
                    _ => false,
                };

                if (internalToThis && !member.Name.Contains('<', StringComparison.Ordinal))
                {
                    yield return (type.Name, member.Name);
                }
            }
        }
    }

    /// <summary>Whether a file names a member, as a whole word rather than as a substring.</summary>
    /// <param name="text">The file.</param>
    /// <param name="member">The member's name.</param>
    /// <returns><see langword="true"/> when the name appears on its own.</returns>
    /// <remarks>
    /// <b>It was <c>Contains</c>, and that made this check quietly weaker than it reads.</b> A
    /// member called <c>Guard</c> was reported as used by every file mentioning <c>GuardedList</c>;
    /// one called <c>Agent</c> by every file mentioning <c>AgentsRegion</c>. Both were dead, and
    /// both passed. Matching on word boundaries is the difference between a check that looks for
    /// the member and one that looks for its letters — found by lens 3 asking what this test
    /// cannot see, rather than by reading the members it names.
    /// </remarks>
    private static bool Names(string text, string member) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(member)}\b", RegexOptions.CultureInvariant, Timeout);

    /// <summary>How long a single match may take.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Every_internal_member_here_is_named_by_a_file_other_than_its_own()
    {
        var sources = Sources();

        var unused = Declared()
            .Where(member => sources.Count(file => Names(file.Text, member.Member)) < 2)
            .Where(member => !SecondTypeInItsOwnFile.Contains(
                $"{member.Type}.{member.Member}", StringComparer.Ordinal))
            .Select(member => $"{member.Type}.{member.Member}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unused.Count == 0,
            "Nothing outside their own file names these, so they are dead or should be private: "
            + string.Join(", ", unused));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity in both halves: reflection really found members, and the files really have
        // text in them. A root that stopped resolving would make every member look used.
        var declared = Declared().ToList();

        Assert.NotEmpty(declared);
        Assert.Contains(declared, member => member is { Type: "Shell", Member: "Settled" });
        Assert.Contains(Sources(), file => file.Name == "Shell.cs" && file.Text.Length > 1000);
    }
}
