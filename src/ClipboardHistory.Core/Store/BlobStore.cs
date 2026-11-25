using Waigoma.ClipboardHistory.Capture;

namespace Waigoma.ClipboardHistory.Store;

// Blobs are named by content hash, so storing the same bytes again is a no-op and formats dedupe for free.
public sealed class BlobStore
{
    private readonly StorePaths _paths;

    public BlobStore(StorePaths paths)
    {
        _paths = paths;
    }

    public string Save(ReadOnlySpan<byte> data, string extension)
    {
        string name = $"{ContentHasher.ForBytes(data)}.{extension}";
        string target = FullPath(name);
        if (File.Exists(target))
        {
            // The sweeper deletes old files it finds unreferenced, and this blob is about to be referenced by a row
            // that is not committed yet. Touching it keeps the sweeper from deleting it in that window.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return name;
        }

        Directory.CreateDirectory(_paths.Temp);
        string temp = Path.Combine(_paths.Temp, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
            }

            // A rename within one volume is atomic, so a reader never sees a half-written blob.
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // A concurrent writer stored identical bytes first, so its copy stands.
        }
        finally
        {
            // File.Delete ignores a missing file, which is the normal case after a successful move.
            File.Delete(temp);
        }

        return name;
    }

    public string FullPath(string fileName) => Path.Combine(_paths.Files, fileName);
}
