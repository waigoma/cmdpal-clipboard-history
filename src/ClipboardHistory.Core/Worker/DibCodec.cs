using System.Buffers.Binary;

namespace Waigoma.ClipboardHistory.Worker;

// Clipboard DIBs have no file header, so ToBmp adds one for the decoder, and BuildV5 writes the header that paste targets expect.
public static class DibCodec
{
    private const int FileHeaderSize = 14;
    private const int V5HeaderSize = 124;
    private const int MaxDimension = 32767;
    private const uint MaxHighDepthColors = 256;

    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const uint BiAlphaBitfields = 6;

    private const uint RedMask = 0x00FF0000;
    private const uint GreenMask = 0x0000FF00;
    private const uint BlueMask = 0x000000FF;
    private const uint AlphaMask = 0xFF000000;

    // 0x73524742 is the LCS_sRGB colour space tag ('sRGB' as a little-endian DWORD).
    private const uint LcsSRgb = 0x73524742;
    private const uint LcsGmImages = 4;

    public static byte[] ToBmp(ReadOnlySpan<byte> dib)
    {
        Layout layout = Layout.Parse(dib);
        var bmp = new byte[FileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        // The offset must point past the header, the masks and the palette, or WIC reads the masks as pixels.
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)(FileHeaderSize + layout.PixelOffset));
        dib.CopyTo(bmp.AsSpan(FileHeaderSize));
        return bmp;
    }

    // Decodes the 32 bpp layouts that WIC gets wrong (it often drops or mangles alpha), and reports false for
    // everything else so the caller can fall back to WIC.
    public static bool TryDecode32(ReadOnlySpan<byte> dib, out int width, out int height, out byte[] bgraTopDown)
    {
        Layout layout = Layout.Parse(dib);
        width = 0;
        height = 0;
        bgraTopDown = Array.Empty<byte>();
        if (layout.BitCount != 32)
        {
            return false;
        }

        bool hasAlpha;
        if (layout.Compression == BiRgb)
        {
            // BI_RGB has no alpha channel, so its fourth byte is padding.
            hasAlpha = false;
        }
        else if (layout.Compression == BiBitfields)
        {
            if (ReadUInt32(dib, 40) != RedMask || ReadUInt32(dib, 44) != GreenMask || ReadUInt32(dib, 48) != BlueMask)
            {
                return false;
            }

            uint alpha = layout.HeaderSize >= 56 ? ReadUInt32(dib, 52) : 0;
            if (alpha is not (0 or AlphaMask))
            {
                return false;
            }

            hasAlpha = alpha == AlphaMask;
        }
        else
        {
            return false;
        }

        int w = layout.Width;
        int h = Math.Abs(layout.Height);
        bool bottomUp = layout.Height > 0;
        var pixels = new byte[(long)w * h * 4];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            int sourceRow = bottomUp ? h - 1 - y : y;
            ReadOnlySpan<byte> source = dib.Slice(layout.PixelOffset + sourceRow * (int)layout.Stride, w * 4);
            Span<byte> target = pixels.AsSpan(y * w * 4, w * 4);
            for (int i = 0; i < source.Length; i += 4)
            {
                target[i] = source[i];
                target[i + 1] = source[i + 1];
                target[i + 2] = source[i + 2];
                target[i + 3] = hasAlpha ? source[i + 3] : (byte)255;
                anyAlpha |= hasAlpha && source[i + 3] != 0;
            }
        }

        // A BI_BITFIELDS image whose alpha is all zero is an opaque image with an unused channel, which is what
        // most producers mean by it, so it is shown opaque instead of invisible.
        if (hasAlpha && !anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        width = w;
        height = h;
        bgraTopDown = pixels;
        return true;
    }

    // The output is bottom-up as the clipboard expects, with straight alpha in the standard BGRA masks.
    public static byte[] BuildV5(int width, int height, ReadOnlySpan<byte> bgraTopDown)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);
        long rowBytes = (long)width * 4;
        long imageBytes = rowBytes * height;
        if (bgraTopDown.Length != imageBytes)
        {
            throw new ArgumentException("Pixel buffer does not match the dimensions.", nameof(bgraTopDown));
        }

        var dib = new byte[V5HeaderSize + imageBytes];
        Span<byte> header = dib.AsSpan(0, V5HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], V5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiBitfields);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)imageBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], RedMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], GreenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], BlueMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], AlphaMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], LcsSRgb);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], LcsGmImages);

        for (int y = 0; y < height; y++)
        {
            int sourceRow = height - 1 - y;
            bgraTopDown.Slice(sourceRow * (int)rowBytes, (int)rowBytes)
                .CopyTo(dib.AsSpan(V5HeaderSize + y * (int)rowBytes, (int)rowBytes));
        }

        return dib;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> dib, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(offset, 4));

    // The validated shape of a DIB. Everything that reads pixels goes through Parse, so a bad header or a short buffer
    // is rejected once, before any span is sliced.
    private readonly record struct Layout(int Width, int Height, int BitCount, uint Compression, int HeaderSize, int PixelOffset, long Stride)
    {
        public static Layout Parse(ReadOnlySpan<byte> dib)
        {
            if (dib.Length < 40)
            {
                throw Invalid("DIB is shorter than a BITMAPINFOHEADER.");
            }

            uint headerSize = ReadUInt32(dib, 0);
            if (headerSize is not (40 or 52 or 56 or 64 or 108 or 124) || headerSize > dib.Length)
            {
                throw Invalid("DIB header size is not one of the known BITMAP header sizes.");
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4, 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8, 4));
            int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
            uint compression = ReadUInt32(dib, 16);
            uint clrUsed = ReadUInt32(dib, 32);

            long absHeight = Math.Abs((long)height);
            if (width is < 1 or > MaxDimension || absHeight is < 1 or > MaxDimension)
            {
                throw Invalid("DIB dimensions are outside 1..32767.");
            }

            if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32))
            {
                throw Invalid("DIB bit count is not supported.");
            }

            // Masks follow a 40-byte header only; the larger headers already contain them.
            long masks = 0;
            if (headerSize == 40)
            {
                masks = compression switch
                {
                    BiBitfields => 12,
                    BiAlphaBitfields => 16,
                    _ => 0,
                };
            }

            long palette = 0;
            if (bitCount <= 8)
            {
                long maxColors = 1L << bitCount;
                // biClrUsed = 0 means the full palette, and a larger count than the bit depth allows is corrupt.
                long colors = clrUsed == 0 ? maxColors : clrUsed;
                if (colors > maxColors)
                {
                    throw Invalid("DIB palette is larger than its bit depth allows.");
                }

                palette = colors * 4;
            }
            else if (clrUsed > 0)
            {
                // A colour table is optional above 8 bpp, but when biClrUsed names one it sits between the masks and
                // the pixels, so the pixel offset must skip it. Real producers stay far below the cap; the buffer check
                // below rejects any table that does not fit.
                if (clrUsed > MaxHighDepthColors)
                {
                    throw Invalid("DIB colour table is larger than a high bit depth image uses.");
                }

                palette = (long)clrUsed * 4;
            }

            long pixelOffset = headerSize + masks + palette;
            long stride = ((long)width * bitCount + 31) / 32 * 4;
            // Compressed (RLE, JPEG, PNG) pixel data has no fixed size, so only the uncompressed layouts can be checked here.
            bool uncompressed = compression is BiRgb or BiBitfields or BiAlphaBitfields;
            if (uncompressed && pixelOffset + stride * absHeight > dib.Length)
            {
                throw Invalid("DIB pixel data is shorter than its dimensions require.");
            }

            if (pixelOffset > dib.Length)
            {
                throw Invalid("DIB has no room for its pixel data.");
            }

            return new Layout(width, height, bitCount, compression, (int)headerSize, (int)pixelOffset, stride);
        }

        private static InvalidDataException Invalid(string message) => new(message);
    }
}
