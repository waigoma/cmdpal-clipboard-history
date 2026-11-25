using System.Buffers.Binary;
using Waigoma.ClipboardHistory.Worker;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class DibCodecTests
{
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;

    [Fact]
    public void ToBmp_24Bpp_PrependsFileHeader_WithPixelOffsetPastTheInfoHeader()
    {
        // Two pixels per row is 6 bytes, padded to 8.
        byte[] dib = Dib(40, 2, 2, 24, BiRgb, 0, new byte[16]);

        byte[] bmp = DibCodec.ToBmp(dib);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(14 + dib.Length, bmp.Length);
        Assert.Equal(54u, ReadUInt32(bmp, 10));
        Assert.Equal(dib, bmp[14..]);
    }

    [Fact]
    public void ToBmp_32BppBitfields_SkipsTheTwelveMaskBytes()
    {
        byte[] masks = Masks(0x00FF0000, 0x0000FF00, 0x000000FF);
        byte[] dib = Dib(40, 2, 2, 32, BiBitfields, 0, Concat(masks, new byte[16]));

        byte[] bmp = DibCodec.ToBmp(dib);

        Assert.Equal(14u + 40u + 12u, ReadUInt32(bmp, 10));
    }

    [Fact]
    public void ToBmp_8BppWithZeroClrUsed_SkipsAll256PaletteEntries()
    {
        byte[] dib = Dib(40, 2, 2, 8, BiRgb, 0, new byte[256 * 4 + 8]);

        byte[] bmp = DibCodec.ToBmp(dib);

        Assert.Equal(14u + 40u + 1024u, ReadUInt32(bmp, 10));
    }

    [Fact]
    public void ToBmp_V5Header_PointsPastTheWholeV5Header()
    {
        byte[] header = Dib(124, 1, 1, 32, BiBitfields, 0, new byte[4]);
        WriteUInt32(header, 40, 0x00FF0000);
        WriteUInt32(header, 44, 0x0000FF00);
        WriteUInt32(header, 48, 0x000000FF);
        WriteUInt32(header, 52, 0xFF000000);

        byte[] bmp = DibCodec.ToBmp(header);

        Assert.Equal(14u + 124u, ReadUInt32(bmp, 10));
    }

    [Fact]
    public void ToBmp_ShorterThanInfoHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DibCodec.ToBmp(new byte[39]));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(39u)]
    [InlineData(41u)]
    [InlineData(999u)]
    public void ToBmp_UnknownHeaderSize_Throws(uint headerSize)
    {
        byte[] dib = Dib(40, 1, 1, 24, BiRgb, 0, new byte[4]);
        WriteUInt32(dib, 0, headerSize);

        Assert.Throws<InvalidDataException>(() => DibCodec.ToBmp(dib));
    }

    [Fact]
    public void ToBmp_TruncatedPixelData_Throws()
    {
        byte[] dib = Dib(40, 2, 2, 24, BiRgb, 0, new byte[8]);

        Assert.Throws<InvalidDataException>(() => DibCodec.ToBmp(dib));
    }

    [Fact]
    public void ToBmp_DimensionOutOfRange_Throws()
    {
        byte[] dib = Dib(40, 32768, 1, 24, BiRgb, 0, new byte[4 * 32768]);

        Assert.Throws<InvalidDataException>(() => DibCodec.ToBmp(dib));
    }

    [Fact]
    public void TryDecode32_BiRgb_IgnoresThePaddingByte()
    {
        // BI_RGB has no alpha, so the fourth byte is padding and must come out opaque.
        byte[] dib = Dib(40, 1, 1, 32, BiRgb, 0, new byte[] { 10, 20, 30, 0 });

        Assert.True(DibCodec.TryDecode32(dib, out int width, out int height, out byte[] bgra));

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, bgra);
    }

    [Fact]
    public void TryDecode32_BitfieldsWithAllZeroAlpha_IsTreatedAsOpaque()
    {
        byte[] dib = V5(1, 2, bottomUp: true, alphaMask: 0xFF000000, new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 });

        Assert.True(DibCodec.TryDecode32(dib, out _, out _, out byte[] bgra));

        Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, bgra);
    }

    [Fact]
    public void TryDecode32_V5WithAlphaMask_KeepsNonZeroAlpha()
    {
        byte[] dib = V5(2, 1, bottomUp: true, alphaMask: 0xFF000000, new byte[] { 1, 2, 3, 128, 4, 5, 6, 0 });

        Assert.True(DibCodec.TryDecode32(dib, out _, out _, out byte[] bgra));

        Assert.Equal(new byte[] { 1, 2, 3, 128, 4, 5, 6, 0 }, bgra);
    }

    [Fact]
    public void TryDecode32_BottomUp_ReturnsRowsTopDown()
    {
        // Stored bottom row first, so the first row in the output must be the last stored row.
        byte[] dib = V5(1, 2, bottomUp: true, alphaMask: 0xFF000000, new byte[] { 9, 9, 9, 255, 1, 1, 1, 255 });

        Assert.True(DibCodec.TryDecode32(dib, out _, out _, out byte[] bgra));

        Assert.Equal(new byte[] { 1, 1, 1, 255, 9, 9, 9, 255 }, bgra);
    }

    [Fact]
    public void TryDecode32_TopDown_KeepsRowOrder()
    {
        byte[] dib = V5(1, 2, bottomUp: false, alphaMask: 0xFF000000, new byte[] { 9, 9, 9, 255, 1, 1, 1, 255 });

        Assert.True(DibCodec.TryDecode32(dib, out _, out _, out byte[] bgra));

        Assert.Equal(new byte[] { 9, 9, 9, 255, 1, 1, 1, 255 }, bgra);
    }

    [Fact]
    public void TryDecode32_24Bpp_ReturnsFalseSoTheCallerUsesWic()
    {
        byte[] dib = Dib(40, 1, 1, 24, BiRgb, 0, new byte[4]);

        Assert.False(DibCodec.TryDecode32(dib, out _, out _, out _));
    }

    [Fact]
    public void TryDecode32_NonStandardMasks_ReturnsFalse()
    {
        byte[] dib = V5(1, 1, bottomUp: true, alphaMask: 0xFF000000, new byte[] { 1, 2, 3, 4 });
        WriteUInt32(dib, 40, 0x000000FF);
        WriteUInt32(dib, 48, 0x00FF0000);

        Assert.False(DibCodec.TryDecode32(dib, out _, out _, out _));
    }

    [Fact]
    public void TryDecode32_ZeroWidth_Throws()
    {
        byte[] dib = Dib(40, 0, 1, 32, BiRgb, 0, new byte[4]);

        Assert.Throws<InvalidDataException>(() => DibCodec.TryDecode32(dib, out _, out _, out _));
    }

    [Fact]
    public void TryDecode32_HeightBeyondLimit_Throws()
    {
        byte[] dib = Dib(40, 1, -32768, 32, BiRgb, 0, new byte[4]);

        Assert.Throws<InvalidDataException>(() => DibCodec.TryDecode32(dib, out _, out _, out _));
    }

    [Fact]
    public void BuildV5_WritesBitfieldsHeaderWithStandardMasksAndSrgb()
    {
        byte[] pixels = { 1, 2, 3, 4 };

        byte[] dib = DibCodec.BuildV5(1, 1, pixels);

        Assert.Equal(124 + 4, dib.Length);
        Assert.Equal(124u, ReadUInt32(dib, 0));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(BiBitfields, ReadUInt32(dib, 16));
        Assert.Equal(0x00FF0000u, ReadUInt32(dib, 40));
        Assert.Equal(0x0000FF00u, ReadUInt32(dib, 44));
        Assert.Equal(0x000000FFu, ReadUInt32(dib, 48));
        Assert.Equal(0xFF000000u, ReadUInt32(dib, 52));
        Assert.Equal(0x73524742u, ReadUInt32(dib, 56));
    }

    [Fact]
    public void BuildV5_ThenTryDecode32_RestoresTopDownPixelsIncludingAlpha()
    {
        byte[] pixels =
        {
            1, 2, 3, 255,   4, 5, 6, 128,
            7, 8, 9, 0,     10, 11, 12, 64,
        };

        byte[] dib = DibCodec.BuildV5(2, 2, pixels);

        Assert.True(DibCodec.TryDecode32(dib, out int width, out int height, out byte[] decoded));
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void BuildV5_MismatchedPixelLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => DibCodec.BuildV5(2, 2, new byte[4]));
    }

    private static byte[] Dib(uint headerSize, int width, int height, ushort bitCount, uint compression, uint clrUsed, byte[] body)
    {
        var bytes = new byte[headerSize + body.Length];
        WriteUInt32(bytes, 0, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), bitCount);
        WriteUInt32(bytes, 16, compression);
        WriteUInt32(bytes, 32, clrUsed);
        body.CopyTo(bytes, (int)headerSize);
        return bytes;
    }

    // A 124-byte V5 header with the given masks, followed by the pixels in the order the caller gives.
    private static byte[] V5(int width, int height, bool bottomUp, uint alphaMask, byte[] pixels)
    {
        byte[] dib = Dib(124, width, bottomUp ? height : -height, 32, BiBitfields, 0, pixels);
        WriteUInt32(dib, 40, 0x00FF0000);
        WriteUInt32(dib, 44, 0x0000FF00);
        WriteUInt32(dib, 48, 0x000000FF);
        WriteUInt32(dib, 52, alphaMask);
        return dib;
    }

    private static byte[] Masks(uint red, uint green, uint blue)
    {
        var masks = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(0), red);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(4), green);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(8), blue);
        return masks;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
