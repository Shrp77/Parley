using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the adapter reports the repository's identifier when it has one, and registers by name alone when it has not
/// (SPEC.md §4.1.1).
/// </summary>
/// <remarks>
/// It is read from the repository's own launch configuration, never from the environment, so no variable can name an
/// identity that the file does not hold.
/// </remarks>
[Integration]
public sealed class AdapterIdentityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-adapter-id-" + Path.GetRandomFileName());

    public AdapterIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ServerOptions Read(string? workspace)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServerOptions.AgentVariable] = "orders@test",
            [ServerOptions.BrokerVariable] = "http://127.0.0.1:1"
        };

        if (workspace is not null)
        {
            map[ServerOptions.WorkspaceVariable] = workspace;
        }

        return ServerOptions.Read(name => map.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public async Task The_identifier_in_the_repository_is_reported()
    {
        var identity = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            Path.Combine(_root, LaunchConfiguration.FileName),
            $$"""{ "mcpServers": {}, "{{LaunchConfiguration.IdentityMember}}": "{{identity}}" }""",
            TestContext.Current.CancellationToken);

        Assert.Equal(identity, Read(_root).Identity);
    }

    [Fact]
    public void A_repository_that_holds_none_reports_none()
    {
        Assert.False(Read(_root).Identity.Known);
    }

    [Fact]
    public void A_launch_with_no_workspace_reports_none()
    {
        Assert.False(Read(null).Identity.Known);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_says_nothing_rather_than_failing_the_launch()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, LaunchConfiguration.FileName), "{ not json", TestContext.Current.CancellationToken);

        Assert.False(Read(_root).Identity.Known);
    }
}
