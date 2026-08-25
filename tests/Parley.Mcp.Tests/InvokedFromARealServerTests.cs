using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The one test that catches the bug that broke invocation completely.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of the invoker passes against the broken version, and that is not a criticism
/// of them — it is a property of the fault. The child process inherits the host's standard input,
/// and for an MCP server that is the live JSON-RPC pipe its client is driving it over. The
/// inherited handle keeps the pipe open, so the invoker waits forever on a process that has
/// already exited.
/// </para>
/// <para>
/// Nothing in-process reproduces that. <c>HeadlessInvokerTests</c> calls the invoker directly and
/// <c>InvokedAnswerTests</c> drives a <c>BrokerSession</c> inside the test host; in both, the
/// parent's standard input is whatever the test runner happens to have, which is not a pipe
/// anybody is waiting on. So this one launches the real <c>parley-mcp</c> binary, holds its
/// standard input open exactly as a client would, and asks whether an invocation completes.
/// </para>
/// <para>
/// It is slower and clumsier than the others and it earns that: removing the stdin redirect makes
/// this fail and leaves the whole rest of the suite green.
/// </para>
/// </remarks>
public sealed class InvokedFromARealServerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-realserver-" + Guid.NewGuid().ToString("N"));

    public InvokedFromARealServerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Finds the server binary built beside these tests.</summary>
    /// <returns>What to run, and a first argument when only the library is present.</returns>
    private static (string FileName, string? Argument) Locate()
    {
        var directory = Path.GetDirectoryName(typeof(InvokedFromARealServerTests).Assembly.Location)!;
        var apphost = Path.Combine(
            directory, OperatingSystem.IsWindows() ? "parley-mcp.exe" : "parley-mcp");

        if (File.Exists(apphost))
        {
            return (apphost, null);
        }

        var library = Path.Combine(directory, "parley-mcp.dll");
        Assert.True(File.Exists(library), $"expected the server beside the tests in {directory}");

        return ("dotnet", library);
    }

    private static Process Launch(string database, string agent, (string, string)[] extra)
    {
        var (fileName, argument) = Locate();
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PARLEY_DB"] = database;
        start.Environment["PARLEY_AGENT"] = agent;
        start.Environment["PARLEY_RESPONDER"] = "test-model";

        foreach (var (name, value) in extra)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start)!;

        Handshake(process);

        return process;
    }

    /// <summary>Completes the MCP initialise exchange, so the server is ready for tool calls.</summary>
    /// <param name="process">The launched server.</param>
    private static void Handshake(Process process)
    {
        Send(process, 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" }
        });

        Read(process, 1);

        process.StandardInput.WriteLine(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        process.StandardInput.Flush();
    }

    private static void Send(Process process, int id, string method, JsonObject parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

        process.StandardInput.WriteLine(request.ToJsonString());
        process.StandardInput.Flush();
    }

    private static JsonNode Read(Process process, int id)
    {
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            var node = JsonNode.Parse(line);

            if (node?["id"]?.GetValue<int>() == id)
            {
                return node;
            }
        }

        throw new JsonException($"the server closed before answering request {id}");
    }

    private static JsonNode CallTool(Process process, int id, string name, JsonObject arguments)
    {
        Send(process, id, "tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments
        });

        var text = Read(process, id)["result"]!["content"]![0]!["text"]!.GetValue<string>();

        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task An_invocation_completes_when_the_hosts_stdin_is_a_live_pipe()
    {
        // The whole point. Both servers below are real processes whose standard input is a pipe
        // this test is holding open, which is the situation a client creates and which no
        // in-process test can imitate. With the invoker's own stdin redirect removed, the child
        // inherits that pipe, the handle keeps it open, and this hangs until the timeout.
        var database = Path.Combine(_root, "parley.db");
        var workspace = Path.Combine(_root, "billing");
        Directory.CreateDirectory(workspace);

        // The runner is parley-mcp itself, and that choice is the test.
        //
        // A child that never touches standard input does not reproduce this at all: `cmd /c echo`
        // completes happily with the pipe inherited. What is needed is a runner that reads stdin
        // and waits on it, which is exactly what an agent runner does -- and the one such binary
        // this repository can rely on being present is its own server. Given a closed stdin it
        // sees end-of-file and exits; given the host's inherited pipe it waits on input meant for
        // somebody else, forever.
        var (runnerFile, runnerArgument) = Locate();
        var runner = runnerArgument is null
            ? Quote(runnerFile)
            : Quote(runnerFile) + " " + Quote(runnerArgument);

        // billing consents, registers, and goes away.
        var billing = Launch(database, "billing",
            [("PARLEY_WORKSPACE", workspace), ("PARLEY_INVOKE_COMMAND", runner)]);

        billing.StandardInput.Close();
        Assert.True(billing.WaitForExit(30_000), "the billing server did not exit");

        // orders stays up, holding its stdin open exactly as a client would.
        var orders = Launch(database, "orders",
            [("PARLEY_INVOKE_COMMAND", runner), ("PARLEY_TICK_SECONDS", "2")]);

        try
        {
            var asked = CallTool(orders, 10, "parley_ask", new JsonObject
            {
                ["to"] = "billing",
                ["intent"] = "Whether tax is frozen at invoice time.",
                ["subQuestions"] = new JsonArray(new JsonObject
                {
                    ["id"] = "q1",
                    ["text"] = "Does billing freeze the rate at invoice time?"
                }),
                ["consequences"] =
                    "If it freezes I stop recomputing and record the contract; if not I keep recomputing."
            });

            Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());

            // Age billing past §14's liveness lease so the sweep treats it as nobody's session.
            AgeOut(database, "billing");

            // Then wait for a sweep to start it and finish. The recorded invocation is the
            // assertion: it is written only after the process has exited and been waited for,
            // which is the step that hangs when the child holds the host's pipe.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            var recorded = false;

            while (DateTime.UtcNow < deadline && !recorded)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
                recorded = InvocationCount(database) > 0;
            }

            Assert.True(
                recorded,
                "no invocation completed in 90 seconds; the invoker is hanging on a child that " +
                "inherited the host's standard input, which is what removing its redirect does");
        }
        finally
        {
            orders.StandardInput.Close();

            if (!orders.WaitForExit(30_000))
            {
                orders.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Quotes a path so the command template survives being split on whitespace.</summary>
    /// <param name="value">The path.</param>
    /// <returns>The path, quoted when it needs to be.</returns>
    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

    /// <summary>Counts invocations recorded so far.</summary>
    /// <param name="database">The database file.</param>
    /// <returns>How many invocations have been written.</returns>
    private static long InvocationCount(string database)
    {
        using var connection = Parley.Store.ParleyDatabase.Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM invocations;";

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Pushes an agent's last-seen time out of the liveness lease.</summary>
    /// <param name="database">The database file.</param>
    /// <param name="agent">The agent to age out.</param>
    private static void AgeOut(string database, string agent)
    {
        using var connection = Parley.Store.ParleyDatabase.Open(database);
        using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE agents SET last_seen_utc = '2020-01-01T00:00:00.0000000+00:00' " +
            "WHERE name = @Agent;";

        command.Parameters.AddWithValue("@Agent", agent);
        command.ExecuteNonQuery();
    }
}
