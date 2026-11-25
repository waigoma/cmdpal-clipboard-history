using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Windows.Storage;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using Waigoma.ClipboardHistory.Store;
using Waigoma.ClipboardHistory.Worker;

namespace Waigoma.ClipboardHistory.Extension;

// One exe serves three roles: the COM server CmdPal starts, the image worker the server starts for itself, and a
// one-shot DIB writer. The roles share no state beyond the arguments, so each returns its own exit code.
internal static class Program
{
    private const string ComServerArgument = "-RegisterProcessAsComServer";
    private const string WorkerArgument = "--worker";
    private const string DibArgument = "--dib";

    private const int ExitUsage = 2;
    private const int ExitFatal = 255;

    // The server reads the processed count from the exit code, so it is capped to keep clear of the fatal code.
    private const int MaxProcessedExitCode = 250;

    [MTAThread]
    public static int Main(string[] args)
    {
        // COM activation appends -Embedding after the registered argument, so the rest of the list is ignored here.
        if (args.Length >= 1 && string.Equals(args[0], ComServerArgument, StringComparison.OrdinalIgnoreCase))
        {
            return RunComServer();
        }

        if (args.Length == 3 && args[0] == WorkerArgument && (args[2] == "0" || args[2] == "1"))
        {
            return RunWorker(args[1], ocr: args[2] == "1");
        }

        if (args.Length == 3 && args[0] == DibArgument)
        {
            return RunDib(args[1], args[2]);
        }

        return ExitUsage;
    }

    private static int RunComServer()
    {
        try
        {
            return RunComServerCore();
        }
        catch (Exception ex)
        {
            // Nothing may leave Main: CmdPal only sees the exit code, and the log is the only place the cause is kept.
            InitializeLogQuietly();
            Log.Error("Program.RunComServer", ex);
            return ExitFatal;
        }
    }

    private static int RunComServerCore()
    {
        using ManualResetEvent extensionDisposedEvent = new(false);

        // One instance is created here and handed back for every request, so the host never sees two extensions.
        ClipboardHistoryExtension extension = new(extensionDisposedEvent);

        ComServer server = new();
        server.RegisterClass<ClipboardHistoryExtension, IExtension>(() => extension);
        server.Start();

        // Blocks until CmdPal disposes the extension, which signals the event after the host has been torn down.
        extensionDisposedEvent.WaitOne();
        server.Stop();
        server.UnsafeDispose();
        return 0;
    }

    // The package folder is unavailable outside a packaged run, so a failure here is ignored and the error is dropped.
    private static void InitializeLogQuietly()
    {
        try
        {
            Log.Initialize(ApplicationData.Current.LocalFolder.Path);
        }
        catch (Exception)
        {
        }
    }

    private static int RunWorker(string root, bool ocr)
    {
        // Holding the handle keeps the name taken for this process; a second worker sees it as already existing.
        using Mutex mutex = new(initiallyOwned: false, AppHost.WorkerMutexName, out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        try
        {
            Log.Initialize(root);
            StorePaths paths = new(root);
            paths.EnsureCreated();

            using HistoryStore store = new(paths, TimeProvider.System);
            WorkerRunner runner = new(store, paths, new WinRtImageProcessor(), () => ocr);
            int processed = runner.RunAll();
            return Math.Min(processed, MaxProcessedExitCode);
        }
        catch (Exception ex)
        {
            Log.Error("Program.RunWorker", ex);
            return ExitFatal;
        }
    }

    // The DIB writer logs nothing: its caller reads only the exit code, and a log line would hold a file path.
    private static int RunDib(string pngPath, string outPath)
    {
        try
        {
            if (!File.Exists(pngPath))
            {
                return 1;
            }

            new WinRtImageProcessor().WriteDib(pngPath, outPath);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
