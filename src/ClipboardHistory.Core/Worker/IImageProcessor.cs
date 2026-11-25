namespace Waigoma.ClipboardHistory.Worker;

// Ocr is null when OCR was not run or the engine is missing, and an empty string when it ran and found no text.
// Qr follows the same rule.
public sealed record ImageResult(int Width, int Height, string? Ocr, string? Qr);

public interface IImageProcessor
{
    // The input is a PNG or a raw DIB file. Writes the PNG and a thumbnail whose long side is at most 128 px.
    ImageResult Process(string inputPath, bool inputIsDib, string pngOutPath, string thumbOutPath, bool ocr, bool qr);

    // Writes a BITMAPV5HEADER with 32 bpp BGRA pixels, bottom-up.
    void WriteDib(string pngPath, string dibOutPath);
}
