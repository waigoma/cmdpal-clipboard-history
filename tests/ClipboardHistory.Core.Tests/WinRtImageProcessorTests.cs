using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Waigoma.ClipboardHistory.Worker;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Waigoma.ClipboardHistory.Tests;

// Every image is generated in code inside the test's own temp folder, so no test depends on a file in the repository.
public sealed class WinRtImageProcessorTests
{
    [Fact]
    public void Process_300x200Png_ReturnsSize_AndWritesThumbnailAt128x85()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "in.png");
        string png = Path.Combine(temp.Root, "out.png");
        string thumb = Path.Combine(temp.Root, "thumb.png");
        WritePng(input, 300, 200, (x, y) => (10, 20, 30, 255));
        var processor = new WinRtImageProcessor();

        ImageResult result = processor.Process(input, false, png, thumb, ocr: false, qr: false);

        Assert.Equal(300, result.Width);
        Assert.Equal(200, result.Height);
        Assert.Null(result.Ocr);
        Assert.Null(result.Qr);
        Assert.Equal((300, 200), Dimensions(png));
        Assert.Equal((128, 85), Dimensions(thumb));
    }

    [Fact]
    public void Process_SmallImage_IsNotUpscaledIntoTheThumbnail()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "in.png");
        string thumb = Path.Combine(temp.Root, "thumb.png");
        WritePng(input, 40, 30, (x, y) => (1, 2, 3, 255));

        new WinRtImageProcessor().Process(input, false, Path.Combine(temp.Root, "out.png"), thumb, ocr: false, qr: false);

        Assert.Equal((40, 30), Dimensions(thumb));
    }

    [Fact]
    public void Process_QrCode_ReturnsTheEncodedText()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "qr.png");
        WriteQrPng(input, "https://example.com/日本", 300);

        ImageResult result = new WinRtImageProcessor().Process(input, false,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: true);

        Assert.Equal("https://example.com/日本", result.Qr);
    }

    [Fact]
    public void Process_LargeQrCode_IsStillReadAfterDownscaling()
    {
        // Larger than the 2000 px decode limit, so the QR path has to scale before it reads.
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "qr.png");
        WriteQrPng(input, "https://example.com/large", 2400);

        ImageResult result = new WinRtImageProcessor().Process(input, false,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: true);

        Assert.Equal("https://example.com/large", result.Qr);
    }

    [Fact]
    public void Process_ImageWithoutQrCode_ReturnsEmptyString_NotNull()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "plain.png");
        WritePng(input, 64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 0, 255));

        ImageResult result = new WinRtImageProcessor().Process(input, false,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: true);

        Assert.Equal(string.Empty, result.Qr);
    }

    [Fact]
    public void Process_OcrRequested_ReturnsNullOrText()
    {
        // The OCR language pack may be absent on the machine, so only the shape of the answer is checked.
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "text.png");
        WritePng(input, 120, 40, (x, y) => (255, 255, 255, 255));

        ImageResult result = new WinRtImageProcessor().Process(input, false,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: true, qr: false);

        Assert.True(result.Ocr is null || result.Ocr is string);
    }

    [Fact]
    public void Process_CorruptPng_Throws()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string input = Path.Combine(temp.Root, "broken.png");
        File.WriteAllText(input, "this is not a png");

        Assert.ThrowsAny<Exception>(() => new WinRtImageProcessor().Process(input, false,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: false));
    }

    [Fact]
    public void WriteDib_ThenProcessDib_RestoresEveryPixel_IncludingTransparency()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        string source = Path.Combine(temp.Root, "source.png");
        string dib = Path.Combine(temp.Root, "clip.dib");
        string restored = Path.Combine(temp.Root, "restored.png");
        (byte B, byte G, byte R, byte A)[] expected =
        {
            (1, 2, 3, 255), (4, 5, 6, 128), (7, 8, 9, 0), (10, 11, 12, 64),
            (13, 14, 15, 200), (16, 17, 18, 1), (19, 20, 21, 254), (22, 23, 24, 100),
            (25, 26, 27, 255), (28, 29, 30, 30), (31, 32, 33, 90), (34, 35, 36, 180),
        };
        WritePng(source, 4, 3, (x, y) => expected[y * 4 + x]);
        var processor = new WinRtImageProcessor();

        processor.WriteDib(source, dib);
        processor.Process(dib, true, restored, Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: false);

        byte[] expectedBytes = expected.SelectMany(p => new[] { p.B, p.G, p.R, p.A }).ToArray();
        Assert.Equal(expectedBytes, ReadPixels(restored));
    }

    [Fact]
    public void Process_DibWithoutAlphaMask_IsDecodedByWic()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        // A 24 bpp DIB takes the WIC path, because the raw decoder only handles 32 bpp.
        byte[] dib = Bmp24Dib(2, 2);
        string input = Path.Combine(temp.Root, "clip.dib");
        File.WriteAllBytes(input, dib);

        ImageResult result = new WinRtImageProcessor().Process(input, true,
            Path.Combine(temp.Root, "out.png"), Path.Combine(temp.Root, "thumb.png"), ocr: false, qr: false);

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    private static void WritePng(string path, int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte b, byte g, byte r, byte a) = pixel(x, y);
                int i = (y * width + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = a;
            }
        }

        SavePng(path, bgra, width, height);
    }

    private static void WriteQrPng(string path, string text, int size)
    {
        var hints = new Dictionary<EncodeHintType, object> { { EncodeHintType.CHARACTER_SET, "UTF-8" } };
        BitMatrix matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints);
        byte[] bgra = new byte[matrix.Width * matrix.Height * 4];
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                byte v = matrix[x, y] ? (byte)0 : (byte)255;
                int i = (y * matrix.Width + x) * 4;
                bgra[i] = v;
                bgra[i + 1] = v;
                bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        }

        SavePng(path, bgra, matrix.Width, matrix.Height);
    }

    private static void SavePng(string path, byte[] bgra, int width, int height)
    {
        SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Straight);
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using IRandomAccessStream output = file.AsRandomAccessStream();
        BitmapEncoder encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        bitmap.Dispose();
    }

    private static (int Width, int Height) Dimensions(string path)
    {
        using SoftwareBitmap bitmap = Decode(path);
        return (bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static byte[] ReadPixels(string path)
    {
        using SoftwareBitmap bitmap = Decode(path);
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        return buffer.ToArray();
    }

    private static SoftwareBitmap Decode(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IRandomAccessStream stream = file.AsRandomAccessStream();
        BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight).AsTask().GetAwaiter().GetResult();
    }

    // A minimal 24 bpp DIB with a bottom-up layout, so the WIC fallback sees a real BMP body.
    private static byte[] Bmp24Dib(int width, int height)
    {
        int stride = (width * 3 + 3) / 4 * 4;
        var dib = new byte[40 + stride * height];
        dib[0] = 40;
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (int i = 40; i < dib.Length; i++)
        {
            dib[i] = (byte)(i * 7);
        }

        return dib;
    }
}
