using System.Text;
using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a file larger than <see cref="StrictJson.MostBytes"/> is refused as unreadable, in fixed words and without
/// being read, wherever a repository's or a node's JSON is read.
/// </summary>
/// <remarks>
/// Lens 2 run 139: a repository's <c>.mcp.json</c> past about a billion characters threw
/// <see cref="OutOfMemoryException"/> out of the read, past every catch, through the node's catalogue scan, and ended
/// the node with every resident agent on the machine.
/// </remarks>
[Unit]
public sealed class AnOversizedFileIsRefusedUnreadTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-oversized-" + Guid.NewGuid().ToString("N"));

    public AnOversizedFileIsRefusedUnreadTests() => Directory.CreateDirectory(_workspace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_file_one_byte_over_the_bound_is_refused_in_fixed_words()
    {
        var path = Sized("over.json", StrictJson.MostBytes + 1);

        var refused = Assert.Throws<JsonException>(() => StrictJson.ReadFile(path));

        Assert.Equal(StrictJson.TooLargeMessage, refused.Message);
    }

    [Fact]
    public async Task A_file_one_byte_over_the_bound_is_refused_when_read_asynchronously()
    {
        var path = Sized("over.json", StrictJson.MostBytes + 1);

        var refused = await Assert.ThrowsAsync<JsonException>(() => StrictJson.ReadFileAsync(path, Ct));

        Assert.Equal(StrictJson.TooLargeMessage, refused.Message);
    }

    [Fact]
    public void A_file_at_the_bound_is_read_whole_and_decoded_as_before()
    {
        var path = Sized("at.json", StrictJson.MostBytes);

        Assert.Equal(File.ReadAllText(path), StrictJson.ReadFile(path));

        var marked = Path.Combine(_workspace, "marked.json");
        File.WriteAllText(marked, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{}", StrictJson.ReadFile(marked));
    }

    [Fact]
    public void A_stream_that_says_it_is_too_long_is_refused_before_one_byte_is_read()
    {
        using var huge = new Unreadable(length: 2L * int.MaxValue);

        var refused = Assert.Throws<JsonException>(() => StrictJson.Read(huge));

        Assert.Equal(StrictJson.TooLargeMessage, refused.Message);
        Assert.Equal(0, huge.Reads);
    }

    [Fact]
    public async Task A_stream_with_no_length_is_read_only_as_far_as_the_bound()
    {
        using var endless = new Endless();

        await Assert.ThrowsAsync<JsonException>(() => StrictJson.ReadAsync(endless, Ct));

        Assert.True(endless.Served <= StrictJson.MostBytes + 81920, $"read {endless.Served} bytes");
    }

    [Fact]
    public void An_oversized_launch_configuration_is_reported_and_not_read()
    {
        Sized(LaunchConfiguration.FileName, StrictJson.MostBytes + 1);

        Assert.False(LaunchConfiguration.TryRead(_workspace, out _, out var problem));
        Assert.Equal(StrictJson.TooLargeMessage, problem);
    }

    [Fact]
    public async Task An_oversized_launch_configuration_is_an_answer_to_the_broker_not_a_throw()
    {
        Sized(LaunchConfiguration.FileName, StrictJson.MostBytes + 1);

        var said = await OwnershipLine.WriteAsync(
            _workspace, null, AgentName.Create("orders@test"), "Owns invoices.", Ct);

        Assert.NotNull(said);
        Assert.Contains(StrictJson.TooLargeMessage, said, StringComparison.Ordinal);
        Assert.Contains("Nothing has been written.", said, StringComparison.Ordinal);
    }

    // A JSON object padded with spaces to exactly the size asked for.
    private string Sized(string name, int bytes)
    {
        var path = Path.Combine(_workspace, name);
        var text = new StringBuilder(bytes).Append('{').Append(' ', bytes - 2).Append('}').ToString();

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    // A stream that claims a length and counts any attempt to read it.
    private sealed class Unreadable(long length) : Stream
    {
        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;

            return 0;
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A stream with no length that never ends, counting what it has served.
    private sealed class Endless : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Served += count;

            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)' ');
            Served += buffer.Length;

            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
