using Grpc.Net.Client;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node names each launch to the broker and starts the process with the token in its environment (SPEC.md §11.7).
/// </summary>
[Integration]
public sealed class NodeNamesItsLaunchesTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-launch-token").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_started_process_finds_the_token_its_node_reported()
    {
        // The stub writes what it finds in its environment and then answers turns as an agent would.
        var seen = Path.Combine(_workspace, "seen.txt");
        var stub = Path.Combine(_workspace, "stub.ps1");

        await File.WriteAllLinesAsync(stub,
        [
            $"Set-Content -Path '{seen}' -Value $env:{LaunchToken.Variable}",
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"stub\"}'); [Console]::Out.Flush()",
            "while ($null -ne ($line = [Console]::In.ReadLine())) { }",
        ], Ct);

        var nowhere = new Wire.Nodes.NodesClient(GrpcChannel.ForAddress("http://127.0.0.1:1"));
        var said = new StringWriter();
        var launched = await new TurnReporter(nowhere, "m1", said).LaunchAsync(AgentName.Create("billing@m1"));

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@m1"),
            InvocationCommand.Parse("pwsh -NoProfile -NonInteractive -File \"" + stub + "\" --input-format stream-json"),
            _workspace,
            TimeProvider.System,
            launched);

        string? found = null;

        // Up to a minute: PowerShell's first start on a loaded Linux runner took more than the ten seconds this once
        // allowed, twice. The loop ends as soon as the stub has written.
        for (var tries = 0; tries < 600 && found is not { Length: > 0 }; tries++)
        {
            await Task.Delay(100, Ct);

            try
            {
                found = File.Exists(seen) ? (await File.ReadAllTextAsync(seen, Ct)).Trim() : null;
            }
            catch (IOException)
            {
                // Still being written.
            }
        }

        Assert.Equal(launched[LaunchToken.Variable], found);

        // A broker that could not be told never stops the launch; it is said.
        Assert.Contains("could not tell the broker it was starting it", said.ToString(), StringComparison.Ordinal);
    }
}
