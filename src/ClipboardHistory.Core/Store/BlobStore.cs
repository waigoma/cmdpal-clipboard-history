using System.Security.Cryptography;
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
        if (File.Exists(target) && TryTouch(target))
        {
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

    // The worker writes its output to tmp and hands the file over here, so the bytes are never held in memory.
    public string SaveFile(string sourcePath, string extension)
    {
        string name = $"{HashFile(sourcePath)}.{extension}";
        string target = FullPath(name);
        if (File.Exists(target) && TryTouch(target))
        {
            File.Delete(sourcePath);
            return name;
        }

        try
        {
            File.Move(sourcePath, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // A concurrent writer stored identical bytes first, so its copy stands and the source is redundant.
            File.Delete(sourcePath);
        }

        return name;
    }

    public string FullPath(string fileName) => Path.Combine(_paths.Files, fileName);

    // The sweeper deletes old files it finds unreferenced, and a blob that is about to be referenced by an
    // uncommitted row must look young. A file that vanished after the Exists check is reported as not touched,
    // so the caller writes it again instead of failing.
    internal static bool TryTouch(string target)
    {
        try
        {
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
