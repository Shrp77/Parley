using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Client.Files;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// A broker with a file location, agents that place files in it the way an adapter does, and <c>parley files</c> run
/// against it in process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Files are placed over the wire, by an attached agent</b>, rather than written into the store: what the person's
/// commands report is what a placement leaves behind — a row, bytes on disk and a placement count — and a fixture that
/// wrote one of those by hand would be checking a report about a state the broker never produces.
/// </para>
/// <para>
/// <b>Nothing is ever launched.</b> The launcher records what it was asked to open, because a suite that opened an
/// editor would open one on the machine of whoever ran it.
/// </para>
/// </remarks>
internal sealed class FileLocationFixture : IAsyncDisposable
{
    /// <summary>The agent that places files in these tests.</summary>
    public const string Billing = "billing@test";

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly GrpcChannel _channel;
    private readonly Dictionary<string, AsyncServerStreamingCall<Wire.Event>> _attached = new(StringComparer.Ordinal);

    /// <summary>Starts the broker and a client of it.</summary>
    public FileLocationFixture()
    {
        Broker = LoopbackOperator.Start(_connection);
        _channel = GrpcChannel.ForAddress(Broker.Address);
        Client = new BrokerClient(new Wire.Operators.OperatorsClient(_channel));
    }

    /// <summary>The broker.</summary>
    public LoopbackOperator Broker { get; }

    /// <summary>The person's client of it.</summary>
    public IParleyBrokerClient Client { get; }

    /// <summary>Every path the command asked to open, and whether it was a folder.</summary>
    public List<(string Path, bool Folder)> Launched { get; } = [];

    /// <summary>What the person types when asked.</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>The project's name the command is given, or <see langword="null"/>.</summary>
    public string? ProjectName { get; set; }

    /// <summary>The machine the command believes it runs on.</summary>
    public string Machine { get; set; } = TestMachine.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Places a file as an attached agent, and fails the test if it is not placed.</summary>
    /// <param name="agent">The placer.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="content">Its text.</param>
    /// <param name="conversation">The conversation to place it in.</param>
    /// <returns>A task that completes when the file is in the location.</returns>
    public async Task PlaceAsync(string agent, string name, string content, string conversation)
    {
        await AttachAsync(agent);

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        using var call = Broker.Agents.PlaceFile(cancellationToken: Ct);

        await call.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame
            {
                Header = new Wire.PlaceFileHeader { Agent = agent, Name = name, ConversationId = conversation }
            },
            Ct);

        await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(bytes) }, Ct);
        await call.RequestStream.CompleteAsync();

        var reply = await call.ResponseAsync;

        Assert.True(
            reply.Outcome is Wire.PlaceOutcome.Placed or Wire.PlaceOutcome.Replaced,
            string.Create(CultureInfo.InvariantCulture, $"the fixture's placement of {name} came to {reply.Outcome}: {reply.Detail}"));
    }

    /// <summary>Runs <c>parley files</c> with these arguments.</summary>
    /// <param name="arguments">Everything after <c>files</c>.</param>
    /// <returns>The exit code, and what went to standard output and standard error.</returns>
    public async Task<(int Code, string Output, string Error)> RunAsync(params string[] arguments)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new StringReader(Input);

        var launcher = new LocalLauncher(
            (path, folder) =>
            {
                Launched.Add((path, folder));
                return true;
            },
            Machine);

        var code = await new FilesCommand(Client, output, error, input, launcher, ProjectName).RunAsync(arguments, Ct);

        return (code, output.ToString(), error.ToString());
    }

    /// <summary>How many files the location holds.</summary>
    /// <returns>The count.</returns>
    public async Task<int> CountAsync() =>
        (await Client.ListSharedFilesAsync(null, FileLimits.MaximumPageSize, null, Ct)).Page.TotalFiles;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var attachment in _attached.Values)
        {
            attachment.Dispose();
        }

        await _channel.ShutdownAsync();
        _channel.Dispose();

        await Broker.DisposeAsync();
        _connection.Dispose();
    }

    private async Task AttachAsync(string agent)
    {
        if (_attached.ContainsKey(agent))
        {
            return;
        }

        var stream = Broker.Agents.Attach(
            new Wire.AttachRequest
            {
                Agent = agent,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = ProtocolVersion.Current
            },
            cancellationToken: Ct);

        Assert.True(await stream.ResponseStream.MoveNext(Ct), "the attach stream ended before it said anything");

        _attached[agent] = stream;
    }
}
