using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That a model is told what Parley is for, through the protocol rather than through a file.
/// </summary>
/// <remarks>
/// <para>
/// MCP's initialize response carries an optional instructions string. It is the only channel for
/// this that is not vendor-specific — every standing-instruction file belongs to one tool, while
/// this reaches any client that speaks the protocol — and for a long time this server sent none,
/// so every deployment had to explain Parley itself or get an agent that never reached for it.
/// </para>
/// <para>
/// Driven through the real binary and a real handshake, because the property is about what
/// arrives on the wire. Asserting that a constant is non-empty would pass just as well against a
/// server that never put it in the response, which is precisely the state this was in.
/// </para>
/// </remarks>
public sealed class ServerInstructionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-instructions-" + Guid.NewGuid().ToString("N"));

    public ServerInstructionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a pooled connection can still hold the file.
        }
    }

    /// <summary>Launches the server and performs the initialize handshake.</summary>
    /// <returns>The <c>result</c> object of the initialize response.</returns>
    /// <remarks>
    /// A broker has to be running, which it did not before: the server used to open a database
    /// and would start against a path that did not exist yet. It is now a client, and a client
    /// with nothing to connect to exits rather than serving — so a test about what the server
    /// says at initialize has to give it something to attach to first.
    /// </remarks>
    private JsonNode Initialize()
    {
        var runtime = Parley.Server.BrokerRuntime.Open(
            Path.Combine(_directory, "parley.db"),
            Parley.Broker.BrokerOptions.Default,
            TimeProvider.System);

        using var broker = Parley.Server.ParleyServer
            .StartAsync(runtime, "http://127.0.0.1:0").GetAwaiter().GetResult()
            is var served
                ? new Disposer(served)
                : throw new InvalidOperationException("unreachable");

        var (fileName, argument) = ConcurrentLaunchTests.ServerBinary();

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PARLEY_BROKER"] = broker.Server.Addresses[0];
        start.Environment["PARLEY_AGENT"] = "orders@test";
        start.Environment["PARLEY_RESPONDER"] = "some-model-9";

        using var process = Process.Start(start)!;

        process.StandardInput.WriteLine(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
            """);

        process.StandardInput.Flush();

        var line = process.StandardOutput.ReadLine();

        process.StandardInput.Close();

        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.False(string.IsNullOrWhiteSpace(line), "the server sent nothing at all");

        var response = JsonNode.Parse(line!)!;

        Assert.Null(response["error"]);

        return response["result"]!;
    }

    [Fact]
    public void The_initialize_response_carries_instructions()
    {
        var result = Initialize();
        var instructions = result["instructions"]?.GetValue<string>();

        Assert.False(
            string.IsNullOrWhiteSpace(instructions),
            $"initialize carried no instructions: {result.ToJsonString()}");

        Assert.Equal(ParleyInstructions.Text, instructions);
    }

    /// <summary>The size cap as the instruction text would have to write it.</summary>
    /// <remarks>
    /// Derived rather than typed, so raising the cap and forgetting the sentence that tells an
    /// agent what it is fails here instead of leaving every agent working to the old number.
    /// </remarks>
    private static string CapAsWritten =>
        Parley.Core.MessageLimits.MaxPayloadCharacters.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_instructions_say_when_to_reach_for_parley_and_what_it_costs()
    {
        // What a model cannot infer from the tool descriptions: that peers exist at all, that
        // guessing at another repository is the thing to avoid, and that the inbox is pull. The
        // mechanics of each call are left to the tools, which describe themselves.
        foreach (var needed in new[]
                 { "parley_agents", "parley_inbox", CapAsWritten })
        {
            Assert.Contains(needed, ParleyInstructions.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_instructions_stay_within_a_message_of_their_own_budget()
    {
        // A standing charge against every agent's context on every session, so it is held to the
        // same limit a single message is. Exceeding it would make the server's advice about
        // terseness the largest thing it ever sent.
        Assert.True(
            ParleyInstructions.Text.Length <= Parley.Core.MessageLimits.MaxPayloadCharacters,
            $"the instructions are {ParleyInstructions.Text.Length} characters, over the " +
            $"{Parley.Core.MessageLimits.MaxPayloadCharacters} a message is allowed");
    }

    [Fact]
    public void The_instructions_name_no_peer_and_no_path()
    {
        // Which agents exist and where they work are facts about one deployment, and they
        // change. A roster baked into a string sent once at start-up would be stale by the first
        // registration, and a workspace path in it would hand every agent the location of every
        // repository — which the agent-facing listing deliberately withholds.
        foreach (var forbidden in new[] { "billing@test", "inventory@test", "/src", "C:\\" })
        {
            Assert.DoesNotContain(forbidden, ParleyInstructions.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Disposes an async resource from a synchronous scope.</summary>
/// <param name="server">The server to stop.</param>
/// <remarks>
/// The handshake is driven synchronously — it writes a line and reads one — and a
/// <c>using</c> there cannot await. This blocks on the stop instead, which costs milliseconds
/// and keeps the test reading as the sequence it is.
/// </remarks>
internal sealed class Disposer(Parley.Server.ParleyServer server) : IDisposable
{
    /// <summary>The running server.</summary>
    public Parley.Server.ParleyServer Server { get; } = server;

    /// <inheritdoc />
    public void Dispose() => Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
