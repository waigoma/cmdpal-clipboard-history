using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory.Worker;

// One pass over the image jobs. It does not reset running jobs: the main process does that at startup,
// and a second reset here would hand a job that another worker is still running to a third.
public sealed class WorkerRunner
{
    private readonly HistoryStore _store;
    private readonly StorePaths _paths;
    private readonly IImageProcessor _processor;
    private readonly Func<bool> _ocrEnabled;

    public WorkerRunner(HistoryStore store, StorePaths paths, IImageProcessor processor, Func<bool> ocrEnabled)
    {
        _store = store;
        _paths = paths;
        _processor = processor;
        _ocrEnabled = ocrEnabled;
    }

    // Each job is claimed with the id of the one before it, so a job that fails and goes back to pending is not
    // claimed again in this pass. A job that arrives with a smaller id waits for the next pass.
    public int RunAll()
    {
        int processed = 0;
        long afterId = 0;
        while (_store.Jobs.Claim(afterId) is { } job)
        {
            afterId = job.EntryId;
            ProcessJob(job.EntryId);
            processed++;
        }

        return processed;
    }

    private void ProcessJob(long entryId)
    {
        string pngOut = TempPath("png");
        string thumbOut = TempPath("png");
        try
        {
            EntryDetail? detail = _store.Get(entryId);
            if (detail is null)
            {
                // The entry was deleted after it was queued, so there is nothing to store.
                _store.Jobs.Complete(entryId);
                return;
            }

            StoreResult(entryId, detail, pngOut, thumbOut);
            _store.Jobs.Complete(entryId);
        }
        catch (Exception ex)
        {
            Log.Error("WorkerRunner.ProcessJob", ex);
            _store.Jobs.Fail(entryId);
        }
        finally
        {
            // Whatever the processor left behind is removed here, so a failed job does not leave files in tmp.
            TryDelete(pngOut);
            TryDelete(thumbOut);
        }
    }

    private void StoreResult(long entryId, EntryDetail detail, string pngOut, string thumbOut)
    {
        StoredFormat input = ChooseInput(detail);
        bool inputIsDib = input.Name != FormatNames.Png;
        // OCR and QR only make sense for pictures, not for Office content or other entries that carry a PNG.
        bool isImage = detail.Summary.Kind == EntryKind.Image;

        ImageResult result = _processor.Process(
            input.FilePath, inputIsDib, pngOut, thumbOut, ocr: isImage && _ocrEnabled(), qr: isImage);

        // A stored PNG is kept as it is, so the re-encoded output is discarded and only the thumbnail is new.
        string? pngFile = inputIsDib ? _store.Blobs.SaveFile(pngOut, "png") : null;
        string stem = Path.GetFileNameWithoutExtension(inputIsDib ? pngFile! : input.FilePath);
        string thumbName = stem + "_t.png";
        File.Move(thumbOut, Path.Combine(_paths.Thumbs, thumbName), overwrite: true);

        // Clean removes the spaces that OCR puts between Japanese glyphs. A null stays null: OCR did not run.
        string? ocr = result.Ocr is null ? null : OcrText.Clean(result.Ocr);
        _store.CompleteImage(entryId, result.Width, result.Height, pngFile, thumbName, ocr, result.Qr);
    }

    // PNG is preferred because it needs no conversion. Only the chosen format is checked, so a missing file fails
    // the job instead of silently using a different representation.
    private static StoredFormat ChooseInput(EntryDetail detail)
    {
        foreach (string name in new[] { FormatNames.Png, FormatNames.DibV5, FormatNames.Dib })
        {
            StoredFormat? format = detail.Formats.FirstOrDefault(f => f.Name == name);
            if (format is null)
            {
                continue;
            }

            if (!File.Exists(format.FilePath))
            {
                throw new InvalidDataException("image blob is missing.");
            }

            return format;
        }

        throw new InvalidDataException("entry has no image format.");
    }

    private string TempPath(string extension)
    {
        Directory.CreateDirectory(_paths.Temp);
        return Path.Combine(_paths.Temp, Guid.NewGuid().ToString("N") + "." + extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweeper removes leftovers in tmp by age, so a file that cannot be deleted now is not a failure.
        }
    }
}
