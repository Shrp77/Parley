using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That the terminal binds no destructive operator operation.
/// </summary>
/// <remarks>
/// <para>
/// <c>contracts/operations.md</c> excludes <c>Retire</c>, <c>Reinstate</c>, <c>Purge</c>,
/// <c>ForceConclude</c>, <c>Instruct</c> and <c>Prune</c> from this surface on a specific
/// argument: <b>a destructive action taken by a keystroke in a full-screen application is a
/// different risk from one taken by a POST behind a page that describes it first.</b> The web
/// console binds them and is right to; this one would be one arrow key away from purging an
/// agent, and that needs its own argument rather than arriving as a side effect of a navigation
/// feature.
/// </para>
/// <para>
/// <b>Enforced rather than remembered.</b> That exclusion is currently true because nobody wrote
/// the call, which is exactly the kind of fact that stops being true quietly. Every one of these
/// is already sitting on <c>BrokerClient</c>, one method call away from any screen here, so the
/// only thing between the terminal and a purge is that somebody has not typed it. This test is
/// that somebody noticing.
/// </para>
/// <para>
/// A source scan, and deliberately not a reflection one. The operations are ordinary methods on a
/// client this assembly already references, so there is no type to look for and no binding to
/// inspect — the question is whether the call is written, and the source is where that is written.
/// </para>
/// </remarks>
public sealed class SurfaceTests
{
    /// <summary>What contracts/operations.md keeps off this surface.</summary>
    private static readonly string[] Destructive =
    [
        "RetireAsync",
        "ReinstateAsync",
        "PurgeAsync",
        "ForceConcludeAsync",
        "InstructAsync",
        "PruneAsync",
    ];

    [Theory]
    [InlineData("RetireAsync")]
    [InlineData("ReinstateAsync")]
    [InlineData("PurgeAsync")]
    [InlineData("ForceConcludeAsync")]
    [InlineData("InstructAsync")]
    [InlineData("PruneAsync")]
    public void The_terminal_calls_no_destructive_operator_operation(string operation)
    {
        var offenders = Sources()
            .Where(file => File.ReadAllText(file.FullName)
                .Contains(operation, StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{operation} is destructive and contracts/operations.md keeps it off this surface. "
            + $"It appears in: {string.Join(", ", offenders)}. If it belongs here now, that is a "
            + "decision to argue in the contract first, not a call to add.");
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // The guard against a guard that passes by finding no files. A path that stopped
        // resolving would make every case above hold vacuously, which is the failure mode this
        // repository has hit before -- a regex that silently matched nothing, making every check
        // built on it pass.
        var sources = Sources();

        Assert.NotEmpty(sources);

        // And the scan can find a name when one is there, so "not found" means absent rather
        // than unsearchable.
        Assert.Contains(
            sources,
            file => File.ReadAllText(file.FullName)
                .Contains("RegistryAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_destructive_operation_named_here_still_exists_to_be_excluded()
    {
        // The other way this rots: an operation renamed on BrokerClient leaves this checking for
        // a string nothing could contain, and the exclusion silently stops being enforced while
        // still reporting success.
        var client = typeof(Parley.Client.BrokerClient)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var operation in Destructive)
        {
            Assert.True(
                client.Contains(operation),
                $"{operation} is no longer on BrokerClient, so excluding it from the terminal "
                + "checks nothing. Rename it here, or drop it if the operation is gone.");
        }
    }

    /// <summary>Every source file the terminal surface is made of.</summary>
    /// <returns>The files.</returns>
    private static IReadOnlyList<FileInfo> Sources()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        var top = new DirectoryInfo(Path.Combine(directory!, "src", "Parley.Cli", "Top"));

        Assert.True(top.Exists, $"{top.FullName} is not there, so this check reads nothing");

        return top.GetFiles("*.cs", SearchOption.AllDirectories);
    }
}
