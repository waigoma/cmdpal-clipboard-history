using System.Globalization;
using System.Text;

namespace Waigoma.ClipboardHistory;

// Never throws: a logging failure must not escape into a COM callback, thread or timer.
public static class Log
{
    private const string FileName = "log.txt";
    private const string RotatedFileName = "log.1.txt";
    private const long MaxBytes = 1024 * 1024;

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
                }

                builder.AppendLine();

                string path = Path.Combine(_directory, FileName);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    File.Move(path, Path.Combine(_directory, RotatedFileName), overwrite: true);
                }

                File.AppendAllText(path, builder.ToString(), Utf8NoBom);
            }
            catch (Exception)
            {
                // Dropping one line is preferable to failing the caller.
            }
        }
    }
}
