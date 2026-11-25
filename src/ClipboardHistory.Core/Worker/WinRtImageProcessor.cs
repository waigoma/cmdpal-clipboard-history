using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Waigoma.ClipboardHistory.Worker;

// Image work through WIC and the WinRT imaging APIs. The worker has no UI thread to keep responsive, so each
// asynchronous WinRT call is blocked on directly.
public sealed class WinRtImageProcessor : IImageProcessor
{
    private const uint ThumbnailLongSide = 128;

    // ZXing decodes large images slowly and QR codes shown on screen are readable well below this.
    private const uint QrLongSide = 2000;

    private readonly Lazy<OcrEngine?> _ocrEngine = new(OcrEngine.TryCreateFromUserProfileLanguages);
    private readonly Func<SoftwareBitmap, string, string?> _ocrStep;
    private readonly Func<SoftwareBitmap, string, string> _qrStep;

    public WinRtImageProcessor()
        : this(null, null)
    {
    }

    // The OCR and QR steps are replaceable so a test can make one of them fail; production always uses the defaults.
    internal WinRtImageProcessor(Func<SoftwareBitmap, string, string?>? ocrStep, Func<SoftwareBitmap, string, string>? qrStep)
    {
        _ocrStep = ocrStep ?? RunOcr;
        _qrStep = qrStep ?? RunQr;
    }

    public ImageResult Process(string inputPath, bool inputIsDib, string pngOutPath, string thumbOutPath, bool ocr, bool qr)
    {
        using SoftwareBitmap bitmap = DecodeInput(inputPath, inputIsDib);
        uint width = (uint)bitmap.PixelWidth;
        uint height = (uint)bitmap.PixelHeight;
        WritePng(bitmap, pngOutPath, width, height);

        (uint thumbWidth, uint thumbHeight) = Fit(width, height, ThumbnailLongSide);
        WritePng(bitmap, thumbOutPath, thumbWidth, thumbHeight);

        string? ocrText = ocr ? Guarded("WinRtImageProcessor.Ocr", () => _ocrStep(bitmap, pngOutPath)) : null;
        string? qrText = qr ? Guarded("WinRtImageProcessor.Qr", () => _qrStep(bitmap, pngOutPath)) : null;
        return new ImageResult((int)width, (int)height, ocrText, qrText);
    }

    // A failing OCR or QR step loses only its own text: the image, its PNG and its thumbnail are already written.
    private static string? Guarded(string where, Func<string?> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            Log.Error(where, ex);
            return null;
        }
    }

    public void WriteDib(string pngPath, string dibOutPath)
    {
        using SoftwareBitmap bitmap = DecodeInput(pngPath, inputIsDib: false);
        byte[] pixels = CopyPixels(bitmap);
        byte[] dib = DibCodec.BuildV5(bitmap.PixelWidth, bitmap.PixelHeight, pixels);

        // The write goes to a sibling name first, so a reader never sees a half-written DIB.
        string temp = dibOutPath + ".part";
        File.WriteAllBytes(temp, dib);
        File.Move(temp, dibOutPath, overwrite: true);
    }

    // The result is always straight-alpha BGRA, so a transparent image stays transparent through the PNG.
    private static SoftwareBitmap DecodeInput(string inputPath, bool inputIsDib)
    {
        if (!inputIsDib)
        {
            using FileStream file = File.OpenRead(inputPath);
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            return DecodeStream(stream);
        }

        byte[] dib = File.ReadAllBytes(inputPath);
        // WIC drops or misreads alpha for some 32 bpp layouts, so those are decoded here from the raw bits.
        if (DibCodec.TryDecode32(dib, out int width, out int height, out byte[] bgra))
        {
            return SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Straight);
        }

        using IRandomAccessStream bmp = ToRandomAccessStream(DibCodec.ToBmp(dib));
        return DecodeStream(bmp);
    }

    private static SoftwareBitmap DecodeStream(IRandomAccessStream stream)
    {
        BitmapDecoder decoder = Await(BitmapDecoder.CreateAsync(stream));
        return Await(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight));
    }

    // Decodes a PNG at a smaller size than it was written, so OCR and QR never see more pixels than they can use.
    private static SoftwareBitmap DecodeFileScaled(string path, BitmapAlphaMode alpha, uint width, uint height)
    {
        using FileStream file = File.OpenRead(path);
        using IRandomAccessStream stream = file.AsRandomAccessStream();
        BitmapDecoder decoder = Await(BitmapDecoder.CreateAsync(stream));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return Await(decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            alpha,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage));
    }

    private static IRandomAccessStream ToRandomAccessStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            Await(writer.StoreAsync());
            writer.DetachStream();
        }

        stream.Seek(0);
        return stream;
    }

    // The encoder scales during the write, so the thumbnail costs no extra in-memory bitmap.
    private static void WritePng(SoftwareBitmap bitmap, string path, uint width, uint height)
    {
        using FileStream file = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using IRandomAccessStream output = file.AsRandomAccessStream();
        BitmapEncoder encoder = Await(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output));
        if (width != bitmap.PixelWidth || height != bitmap.PixelHeight)
        {
            encoder.BitmapTransform.ScaledWidth = width;
            encoder.BitmapTransform.ScaledHeight = height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        encoder.SetSoftwareBitmap(bitmap);
        Await(encoder.FlushAsync());
    }

    private string? RunOcr(SoftwareBitmap bitmap, string pngPath)
    {
        OcrEngine? engine = _ocrEngine.Value;
        if (engine is null)
        {
            return null;
        }

        uint max = OcrEngine.MaxImageDimension;
        (uint width, uint height) = Fit((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, max);
        using SoftwareBitmap input = width == bitmap.PixelWidth && height == bitmap.PixelHeight
            ? SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            : DecodeFileScaled(pngPath, BitmapAlphaMode.Premultiplied, width, height);
        return Await(engine.RecognizeAsync(input)).Text;
    }

    private static string RunQr(SoftwareBitmap bitmap, string pngPath)
    {
        (uint width, uint height) = Fit((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, QrLongSide);
        byte[] bgra;
        if (width == bitmap.PixelWidth && height == bitmap.PixelHeight)
        {
            bgra = CopyPixels(bitmap);
        }
        else
        {
            using SoftwareBitmap scaled = DecodeFileScaled(pngPath, BitmapAlphaMode.Straight, width, height);
            bgra = CopyPixels(scaled);
        }

        var source = new RGBLuminanceSource(Luminance(bgra), (int)width, (int)height, RGBLuminanceSource.BitmapFormat.Gray8);
        var binary = new BinaryBitmap(new HybridBinarizer(source));
        var hints = new Dictionary<DecodeHintType, object>
        {
            { DecodeHintType.TRY_HARDER, true },
            { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE } },
            { DecodeHintType.CHARACTER_SET, "UTF-8" },
        };
        try
        {
            return new QRCodeReader().decode(binary, hints)?.Text ?? string.Empty;
        }
        catch (ReaderException)
        {
            // ZXing reports "no code here" as an exception, and that is a normal answer rather than a failure.
            return string.Empty;
        }
    }

    private static byte[] CopyPixels(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        return buffer.ToArray();
    }

    // Transparent pixels are composited over white first, because a viewer shows them on a light background.
    private static byte[] Luminance(byte[] bgra)
    {
        var luma = new byte[bgra.Length / 4];
        for (int i = 0, p = 0; i < bgra.Length; i += 4, p++)
        {
            int alpha = bgra[i + 3];
            int b = Composite(bgra[i], alpha);
            int g = Composite(bgra[i + 1], alpha);
            int r = Composite(bgra[i + 2], alpha);
            luma[p] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        return luma;
    }

    private static int Composite(byte channel, int alpha) => (channel * alpha + 255 * (255 - alpha) + 127) / 255;

    // Scales so the longer side is at most longSide, and never enlarges an image that already fits.
    private static (uint Width, uint Height) Fit(uint width, uint height, uint longSide)
    {
        uint current = Math.Max(width, height);
        if (current <= longSide)
        {
            return (width, height);
        }

        double scale = (double)longSide / current;
        uint scaledWidth = Math.Max(1u, (uint)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        uint scaledHeight = Math.Max(1u, (uint)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        return (scaledWidth, scaledHeight);
    }

    private static T Await<T>(Windows.Foundation.IAsyncOperation<T> operation) =>
        operation.AsTask().GetAwaiter().GetResult();

    private static void Await(Windows.Foundation.IAsyncAction action) =>
        action.AsTask().GetAwaiter().GetResult();
}
