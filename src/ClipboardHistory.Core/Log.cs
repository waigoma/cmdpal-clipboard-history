using System.Globalization;
using System.Text;

namespace Waigoma.ClipboardHistory;

// Never throws: a logging failure must not escape into a COM callback, thread or timer.
public static class Log
{
    private const string FileName = "log.txt";
    private const string RotatedFileName = "log.1.txt";
    private const long MaxBytes = 1024 * 1024;

    // Deeper inner chains are cut off, so a pathological exception cannot grow a single log line without bound.
    private const int MaxInnerDepth = 8;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Initialize(string dir)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _directory = dir;
            }
            catch (Exception)
            {
                _directory = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string where, Exception ex) => Write("ERROR", where, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (_directory is null)
            {
                return;
            }

            try
            {
                var builder = new StringBuilder();
                builder.Append(DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture))
                    .Append(' ').Append(level).Append(' ').Append(message);
                if (ex is not null)
                {
                    // Exception messages can embed user paths, so only the type, HResult and stack are written.
                    builder.Append(" type=").Append(ex.GetType().FullName)
                        .Append(" hresult=0x").Append(ex.HResult.ToString("X8", CultureInfo.InvariantCulture))
                        .AppendLine().Append(ex.StackTrace);

                    // The inner chain often holds the real cause (a file lock, for one), so its type and code are kept too.
                    int depth = 0;
                    for (Exception? inner = ex.InnerException; inner is not null && depth < MaxInnerDepth; inner = inner.InnerException, depth++)
                    {
                        builder.AppendLine().Append("  inner type=").Append(inner.GetType().FullName)
                            .Append(" hresult=0x").Append(inner.HResult.ToString("X8", CultureInfo.InvariantCulture));
                    }
                }

                builder.AppendLine();

                string path = Path.Combine(_directory, FileName);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    TryRotate(path);
                }

                File.AppendAllText(path, builder.ToString(), Utf8NoBom);
            }
            catch (Exception)
            {
                // Dropping one line is preferable to failing the caller.
            }
        }
    }

    // A rotation that cannot complete, because the previous log is held open, leaves the file as it is. The line is
    // still appended, so a locked archive never costs the newest entries.
    private static void TryRotate(string path)
    {
        try
        {
            File.Move(path, Path.Combine(_directory!, RotatedFileName), overwrite: true);
        }
        catch (IOException)
        {
            // Skipping the rotation is the whole recovery; logging from here would re-enter Write under the same lock.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
