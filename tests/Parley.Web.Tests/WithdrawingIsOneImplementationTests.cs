using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The terminal and the web console retire, reinstate and purge through one implementation.
/// </summary>
/// <remarks>
/// The owner's rule is that the two surfaces behave the same. A second call to the broker's operation on either surface
/// is how they drifted before: one withdrew the files and asked for the name, the other did neither. So the broker's three
/// operations may be called only from <c>Parley.Setup</c>'s <c>AgentWithdrawal</c>, and each surface must call it.
/// </remarks>
[Unit]
public sealed class WithdrawingIsOneImplementationTests
{
    [Theory]
    [InlineData("src/Parley.Terminal")]
    [InlineData("src/Parley.Web")]
    public void Neither_surface_calls_the_brokers_operation_itself(string surface)
    {
        // Any member access, whatever the receiver: `(broker).`, `broker?.`, `new BrokerClient(...).` and a method group
        // passed as a delegate all count. The first version needed a word right before the dot, and a receiver ending in
        // a bracket walked past it. A bare `RetireAsync(` is a surface's own helper, which is what calls AgentWithdrawal.
        var direct = Sources(surface)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"(?<!AgentWithdrawal\s*)(\?\s*)?\.\s*(RetireAsync|ReinstateAsync|PurgeAsync)\b", RegexOptions.None, TimeSpan.FromSeconds(2))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToList();

        Assert.True(direct.Count == 0, "a surface calls the broker's withdrawal itself rather than AgentWithdrawal: " + string.Join("; ", direct));
    }

    [Theory]
    [InlineData("src/Parley.Terminal/Withdrawing.cs")]
    [InlineData("src/Parley.Web/Pages/Projects/Act.cshtml.cs")]
    public void Each_surface_calls_the_shared_one(string file)
    {
        // A call may break its line before the dot, so whitespace around a dot is read as nothing.
        var text = Regex.Replace(File.ReadAllText(RepositoryPath(file)), @"\s*\.\s*", ".", RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.Contains("AgentWithdrawal.RetireAsync", text, StringComparison.Ordinal);
        Assert.Contains("AgentWithdrawal.PurgeAsync", text, StringComparison.Ordinal);
        Assert.Contains("AgentWithdrawal.ReinstateAsync", text, StringComparison.Ordinal);
        Assert.Contains("AgentWithdrawal.TypedNameProblem", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Sources(string relative) =>
        Directory.GetFiles(RepositoryPath(relative), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
