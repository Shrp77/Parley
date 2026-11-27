using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a repository whose <c>.mcp.json</c> names a member twice is reported and passed over, by every reader of
/// it, and that no file Parley parses is parsed any other way.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 run 137 reproduced it with the real tool: a key written twice made <c>parley enroll &lt;folder&gt; --all
/// --describe</c> crash in the scan, so no sibling could be enrolled, and made <c>parley waiting</c> and
/// <c>parley files</c> crash inside that repository, printing the key's escape sequences to the terminal.
/// <see cref="DuplicatedMembersAreRefusedTests"/> holds the parse itself; this holds the readers that open the file.
/// </para>
/// </remarks>
[Integration]
public sealed class ADuplicatedMemberStopsNoSiblingTests : IDisposable
{
    private const char Escape = (char)0x1B;

    private const string EnvKeyTwice = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp",
          "env": { "PARLEY_AGENT": "orders", "K\u001b[2J": "a", "K\u001b[2J": "b" } } } }
        """;

    private const string TopLevelTwice = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } },
          "x\u001b]0;owned\u0007": 1, "x\u001b]0;owned\u0007": 2 }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("parley-dupkey-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Repo(string name, string launch)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".mcp.json"), launch);
        return dir;
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void A_scan_lists_the_bad_repository_blank_and_still_reads_its_sibling(string bad)
    {
        Repo("aaa-orders", bad);
        Repo("zzz-billing", """
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing" } } } }
            """);

        var found = EnrollCommand.Find(_root);

        Assert.Equal(2, found.Count);
        Assert.Equal("billing", found.Single(e => e.Workspace.EndsWith("zzz-billing", StringComparison.Ordinal)).Agent);
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void A_command_run_inside_the_repository_reads_no_project_rather_than_crashing(string bad)
    {
        Assert.Empty(RepositoryProjects.Read(Repo("orders", bad)));
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void The_launch_configuration_is_unreadable_and_says_so_without_the_key(string bad)
    {
        Assert.False(LaunchConfiguration.TryRead(Repo("orders", bad), out _, out var problem));
        Assert.Equal(StrictJson.DuplicateMessage, problem);
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public async Task Accepting_an_ownership_line_refuses_it_without_the_key(string bad)
    {
        var said = await OwnershipLine.WriteAsync(
            Repo("orders", bad), "alpha", AgentName.Create($"orders@{MachineName.Current}"), "orders, nothing else", Ct);

        Assert.NotNull(said);
        Assert.DoesNotContain(Escape, said);
    }

    [Fact]
    public void Every_json_parse_in_the_shipped_code_goes_through_the_strict_reader()
    {
        // The payload readers are the exception, by name: a payload is the broker's own record of what a tool call
        // carried, and the broker's reading (PayloadShape) and the surfaces' must stay one algorithm, which they are,
        // so neither crashes and both answer the same copy of a repeated member. So is the resident agent's pump: it reads
        // a runner's output, not a file, takes every field at the parse, and must never refuse a result line, or its turn
        // and every turn after fall out of step (lens 1 run 132).
        // Named by path from the repository root, so a file of the same name in another project is not exempt.
        string[] allowed =
        [
            "src/Parley.Configuration/StrictJson.cs",
            "src/Parley.Core/PayloadShape.cs",
            "src/Parley.Client/Rendering/Payload.cs",
            "src/Parley.Client/Rendering/Reading.cs",
            "src/Parley.Headless/ResidentAgent.cs",
        ];

        // Every way the framework reads JSON text: the three documents' parses and their asynchronous and reader forms,
        // the serializer, and the reader itself.
        string[] parses =
        [
            "JsonNode.Parse(", "JsonDocument.Parse(", "JsonObject.Parse(", "ParseAsync(", "ParseValue(",
            "JsonSerializer.Deserialize", "Utf8JsonReader",
        ];
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var offenders = new List<string>();
        var separator = Path.DirectorySeparatorChar;
        var exempt = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                || file.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            if (allowed.Contains(Path.GetRelativePath(root, file).Replace(separator, '/'), StringComparer.Ordinal))
            {
                exempt++;

                continue;
            }

            var number = 0;

            foreach (var line in File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n'))
            {
                number++;

                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && parses.Any(parse => line.Contains(parse, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{number}");
                }
            }
        }

        // Non-vacuity: every exemption still names a file that is there.
        Assert.Equal(allowed.Length, exempt);
        Assert.True(offenders.Count == 0, "parses outside StrictJson: " + string.Join(", ", offenders));
    }
}
