using System.Buffers.Binary;
using System.Text;
using Waigoma.ClipboardHistory.Interop;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class ClipboardReaderTests
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void PngLength_ExactFile_ReturnsWholeLength()
    {
        byte[] png = Png();

        Assert.Equal(png.Length, ClipboardReader.PngLength(png));
    }

    [Fact]
    public void PngLength_TrailingSlackAfterIend_IsExcluded()
    {
        byte[] png = Png();
        byte[] withSlack = png.Concat(new byte[] { 9, 9, 9, 9, 9 }).ToArray();

        Assert.Equal(png.Length, ClipboardReader.PngLength(withSlack));
    }

    [Fact]
    public void PngLength_TruncatedBeforeIendEnds_ReturnsWholeData()
    {
        byte[] truncated = Png()[..^3];

        Assert.Equal(truncated.Length, ClipboardReader.PngLength(truncated));
    }

    [Fact]
    public void PngLength_NotPng_ReturnsWholeData()
    {
        byte[] data = Encoding.ASCII.GetBytes("not a png at all, just some bytes");

        Assert.Equal(data.Length, ClipboardReader.PngLength(data));
    }

    [Fact]
    public void PngLength_ChunkLengthOverflow_ReturnsWholeData()
    {
        byte[] data = Signature.Concat(Chunk("IDAT", Array.Empty<byte>(), declaredLength: 0xFFFFFFFF))
            .Concat(new byte[] { 1, 2, 3 }).ToArray();

        Assert.Equal(data.Length, ClipboardReader.PngLength(data));
    }

    private static byte[] Png()
    {
        return Signature
            .Concat(Chunk("IHDR", new byte[13]))
            .Concat(Chunk("IDAT", new byte[] { 1, 2, 3 }))
            .Concat(Chunk("IEND", Array.Empty<byte>()))
            .ToArray();
    }

    // The CRC is zero: PngLength only walks lengths and types, so a real checksum is not needed here.
    private static byte[] Chunk(string type, byte[] payload, uint? declaredLength = null)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, declaredLength ?? (uint)payload.Length);
        return length
            .Concat(Encoding.ASCII.GetBytes(type))
            .Concat(payload)
            .Concat(new byte[4])
            .ToArray();
    }
}
