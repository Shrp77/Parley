using System.Net;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the person's read of a placed file (<c>BrokerClient.ReadSharedFileAsync</c>, SPEC.md §9.5) keeps only bytes that are
/// the file the header named: bytes whose hash is not the header's, or more bytes than the header said, are refused, and
/// the Media page serves nothing.
/// </summary>
/// <remarks>
/// Review, surface lens (run H on ad5f2a1f): both checks passed every test with the check removed, because every stand-in
/// broker sent the bytes its header described. The second is also what bounds the read: a header that understates the size
/// passes the size limit, and without the check the bytes are then kept whatever their length.
/// </remarks>
[Integration]
public sealed class BytesThatAreNotTheFileAreNotServedTests : IDisposable
{
    private const string Conversation = "pricing";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private readonly FilesConsole _console = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _console.Dispose();

    private static Wire.SharedFileView View(string name, string sha, long size) => new()
    {
        ConversationId = Conversation,
        Folder = Conversation,
        Name = name,
        SizeBytes = size,
        Sha256 = sha,
        PlacedBy = "billing@alpha",
        PlacedUtc = "2026-09-14T09:00:00Z",
    };

    private static Uri Image(string name, string sha) => new(
        $"/Conversations/Media?handler=Image&project={FilesConsole.Project}&id={Conversation}&name={name}&sha={sha}",
        UriKind.Relative);

    [Fact]
    public async Task Bytes_whose_hash_is_not_the_headers_are_not_served()
    {
        var other = Png.ToArray();
        other[^1] ^= 0xFF;
        var sha = ContentHash.Of(Png).Value;
        _console.Broker.Files.Add(View("swapped.png", sha, Png.Length));
        _console.Broker.Contents["swapped.png"] = other;

        using var client = _console.CreateClient();
        using var response = await client.GetAsync(Image("swapped.png", sha), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(other, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task More_bytes_than_the_header_said_are_not_served_even_when_their_hash_is_the_headers()
    {
        byte[] longer = [.. Png, 1, 2, 3, 4];
        var sha = ContentHash.Of(longer).Value;
        _console.Broker.Files.Add(View("longer.png", sha, Png.Length));
        _console.Broker.Contents["longer.png"] = longer;

        using var client = _console.CreateClient();
        using var response = await client.GetAsync(Image("longer.png", sha), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(longer, await response.Content.ReadAsByteArrayAsync(Ct));
    }
}
