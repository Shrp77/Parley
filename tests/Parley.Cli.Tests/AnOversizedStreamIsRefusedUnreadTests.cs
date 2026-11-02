using System.Text.Json;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a stream longer than <see cref="StrictJson.MostBytes"/> is refused as unreadable without being read past the
/// bound.
/// </summary>
/// <remarks>
/// The in-memory half of <see cref="AnOversizedFileIsRefusedUnreadTests"/>: the same bound, held on streams this test
/// makes, so it runs with the unit tests while the file cases run with the integration tests.
/// </remarks>
[Unit]
public sealed class AnOversizedStreamIsRefusedUnreadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
