using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Placing, fetching and reporting files over the wire, and waiting for the location to say something.
/// </summary>
/// <remarks>
/// The steps an adapter takes, written once for the tests that need them on a broker they started themselves — one
/// that outlives a restart, which <see cref="LoopbackBroker"/> cannot, being in memory.
/// </remarks>
internal static class FileWire
{
    /// <summary>Places a file, as an adapter does: a header, then pieces.</summary>
    /// <param name="client">The agent's client.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="conversation">The conversation, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the placement came to.</returns>
    public static async Task<Wire.PlaceFileReply> PlaceAsync(
        Wire.Agents.AgentsClient client,
        string agent,
        string name,
        byte[] content,
        string? conversation,
        CancellationToken cancellationToken)
    {
        using var call = client.PlaceFile(cancellationToken: cancellationToken);

        try
        {
            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame
                {
                    Header = new Wire.PlaceFileHeader { Agent = agent, Name = name, ConversationId = conversation ?? string.Empty }
                },
                cancellationToken);

            for (var offset = 0; offset < content.Length; offset += FileLimits.ChunkBytes)
            {
                await call.RequestStream.WriteAsync(
                    new Wire.PlaceFileFrame
                    {
                        Chunk = ByteString.CopyFrom(content, offset, Math.Min(FileLimits.ChunkBytes, content.Length - offset))
                    },
                    cancellationToken);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (Exception closed) when (closed is RpcException or InvalidOperationException)
        {
            // The broker answered before reading every piece.
        }

        return await call.ResponseAsync;
    }

    /// <summary>Fetches a file and reports it, as an adapter does once it has written and verified the bytes.</summary>
    /// <param name="client">The agent's client.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="conversation">The conversation, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The header of what was fetched.</returns>
    /// <exception cref="InvalidOperationException">The fetch was refused, or the report was.</exception>
    public static async Task<Wire.SharedFileView> FetchAndReportAsync(
        Wire.Agents.AgentsClient client,
        string agent,
        string name,
        string? conversation,
        CancellationToken cancellationToken)
    {
        using var call = client.FetchFile(
            new Wire.FetchFileRequest { Agent = agent, Name = name, ConversationId = conversation ?? string.Empty },
            cancellationToken: cancellationToken);

        Wire.SharedFileView? header = null;

        await foreach (var frame in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            if (frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal)
            {
                throw new InvalidOperationException($"The fixture's fetch was refused: {frame.Refusal.Rejection}.");
            }

            header ??= frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Header ? frame.Header : null;
        }

        var reported = await client.FetchedFileAsync(
            new Wire.FetchedFileRequest { Agent = agent, ConversationId = header!.ConversationId, Name = header.Name, Sha256 = header.Sha256 },
            cancellationToken: cancellationToken);

        return reported.Ok ? header : throw new InvalidOperationException("The fixture's fetch was not recorded.");
    }

    /// <summary>Waits until the person's list of the location satisfies a condition.</summary>
    /// <param name="operators">The operator's client.</param>
    /// <param name="until">What the list must say.</param>
    /// <param name="within">How long to allow.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The list, once it says it.</returns>
    /// <exception cref="TimeoutException">It never did.</exception>
    /// <remarks>
    /// Polled, because what is being waited on is the broker noticing a change on disk, which it does on its own time.
    /// The list itself reconciles before it answers, so the wait is also a check that the reconcile works.
    /// </remarks>
    public static async Task<IReadOnlyList<Wire.SharedFileView>> WaitForAsync(
        Wire.Operators.OperatorsClient operators,
        Func<IReadOnlyList<Wire.SharedFileView>, bool> until,
        TimeSpan within,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + within;

        while (true)
        {
            var listed = await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: cancellationToken);

            if (until(listed.Files))
            {
                return listed.Files;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The file location never showed what the test was waiting for.");
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
