using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Parley.Screens;

/// <summary>Rewrites a browser's PNG as an opaque RGB image holding nothing but its pixels.</summary>
/// <remarks>
/// <para>
/// <b>Rewritten, not filtered.</b> The site refuses a PNG with text, EXIF or time chunks, data after its end, or
/// transparency. Dropping chunks would leave an RGBA image whose alpha channel says nothing, so the pixels are decoded
/// and encoded again as RGB with only <c>IHDR</c>, <c>IDAT</c> and <c>IEND</c>. The same pixels always give the same
/// bytes, so a shot that did not change keeps its hash between runs.
/// </para>
/// <para>
/// Only what a browser writes is read: 8 bits a sample, RGB or RGBA, not interlaced. Anything else is refused with what
/// it was, rather than guessed at.
/// </para>
/// </remarks>
internal static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The size an encoded image says it is.</summary>
    /// <param name="png">A PNG.</param>
    /// <returns>Its width and height.</returns>
    public static (int Width, int Height) SizeOf(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        return png.Length >= 24 && png.AsSpan(0, 8).SequenceEqual(Signature)
            ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)))
            : throw new InvalidDataException("not a PNG");
    }

    /// <summary>Decodes <paramref name="png"/> and encodes it again as opaque RGB with no metadata.</summary>
    /// <param name="png">What the browser wrote.</param>
    /// <returns>The rewritten image.</returns>
    /// <exception cref="InvalidDataException">The image is not one this reads, or is not fully opaque.</exception>
    public static byte[] Strip(byte[] png)
    {
        var (width, height, channels, pixels) = Decode(png);
        var rgb = channels == 3 ? pixels : DropAlpha(pixels, width, height);

        return Encode(width, height, rgb);
    }

    private static (int Width, int Height, int Channels, byte[] Pixels) Decode(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();

        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));

            if (length < 0 || length > png.Length - at - 12)
            {
                throw new InvalidDataException($"a PNG chunk at byte {at} runs past the end");
            }

            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);

            if (string.Equals(type, "IHDR", StringComparison.Ordinal))
            {
                if (length != 13)
                {
                    throw new InvalidDataException($"a PNG IHDR of {length} bytes");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                var (depth, colour, interlace) = (data[8], data[9], data[12]);

                if (depth != 8 || colour is not (2 or 6) || interlace != 0)
                {
                    throw new InvalidDataException($"a PNG of depth {depth}, colour type {colour}, interlace {interlace}: only 8-bit RGB or RGBA, not interlaced");
                }

                channels = colour == 2 ? 3 : 4;
            }
            else if (string.Equals(type, "IDAT", StringComparison.Ordinal))
            {
                compressed.Write(data);
            }
            else if (string.Equals(type, "IEND", StringComparison.Ordinal))
            {
                break;
            }

            at += 12 + length;
        }

        if (channels == 0)
        {
            throw new InvalidDataException("a PNG with no IHDR");
        }

        // Far above any shot the list allows, and small enough that the buffers below cannot overflow.
        if (width is < 1 or > 16_384 || height is < 1 or > 16_384)
        {
            throw new InvalidDataException($"a PNG of {width}x{height}");
        }

        compressed.Position = 0;
        var stride = width * channels;
        var filtered = new byte[(stride + 1) * height];

        try
        {
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            inflate.ReadExactly(filtered);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("a PNG whose image data ends early");
        }

        return (width, height, channels, Unfilter(filtered, stride, height, channels));
    }

    private static byte[] Unfilter(byte[] filtered, int stride, int height, int bpp)
    {
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (stride + 1)];
            var source = filtered.AsSpan((y * (stride + 1)) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var above = y == 0 ? default : pixels.AsSpan((y - 1) * stride, stride);

            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0;
                int b = y > 0 ? above[x] : 0;
                int c = x >= bpp && y > 0 ? above[x - bpp] : 0;

                row[x] = (byte)(source[x] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter} on row {y}"),
                });
            }
        }

        return pixels;
    }

    private static byte[] DropAlpha(byte[] rgba, int width, int height)
    {
        var rgb = new byte[width * height * 3];

        for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 3)
        {
            if (rgba[i + 3] != 255)
            {
                var pixel = i / 4;
                throw new InvalidDataException($"the capture is transparent at {pixel % width},{pixel / width}");
            }

            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }

        return rgb;
    }

    private static byte[] Encode(int width, int height, byte[] rgb)
    {
        const int Bpp = 3;
        var stride = width * Bpp;
        var filtered = new byte[(stride + 1) * height];
        var trial = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var row = rgb.AsSpan(y * stride, stride);
            var above = y == 0 ? default : rgb.AsSpan((y - 1) * stride, stride);
            var best = long.MaxValue;
            var chosen = 0;

            // Each row takes the filter whose output is smallest in absolute value, the usual way to give deflate the
            // runs it compresses best. Writing the winner a second time is cheaper than keeping five rows.
            for (var filter = 0; filter <= 4; filter++)
            {
                var cost = Filter(filter, row, above, trial, Bpp);

                if (cost < best)
                {
                    (best, chosen) = (cost, filter);
                }
            }

            filtered[y * (stride + 1)] = (byte)chosen;
            _ = Filter(chosen, row, above, filtered.AsSpan((y * (stride + 1)) + 1, stride), Bpp);
        }

        using var idat = new MemoryStream();

        using (var deflate = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(filtered);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var output = new MemoryStream();
        output.Write(Signature);
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", idat.ToArray());
        Chunk(output, "IEND", []);

        return output.ToArray();
    }

    private static long Filter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, Span<byte> into, int bpp)
    {
        long cost = 0;

        for (var x = 0; x < row.Length; x++)
        {
            int a = x >= bpp ? row[x - bpp] : 0;
            int b = above.IsEmpty ? 0 : above[x];
            int c = x >= bpp && !above.IsEmpty ? above[x - bpp] : 0;

            var value = (byte)(row[x] - filter switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            });

            into[x] = value;
            cost += value < 128 ? value : 256 - value;
        }

        return cost;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        var name = Encoding.ASCII.GetBytes(type);

        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        output.Write(four);
        output.Write(name);
        output.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(Crc(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu);
        output.Write(four);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var octet in bytes)
        {
            crc = CrcTable[(crc ^ octet) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (var n = 0u; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
