using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Waigoma.ClipboardHistory.Capture;

// Reads the Windows clipboard history (Win+V). The WinRT calls need an STA thread, and a wait with a timeout keeps a
// hung shell from blocking the caller. Failures and timeouts yield an empty list rather than an exception.
public sealed class WindowsHistorySource : IBackfillSource
{
    // Bounds the whole read, so a slow history cannot delay startup for long.
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    // Each WinRT await is bounded separately, so one stuck call cannot hold the STA thread past the overall timeout.
    private static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<BackfillItem> ReadNewerThan(DateTimeOffset since)
    {
        // The worker writes only into its own TaskCompletionSource. After a timeout it may finish later and
        // the result is simply dropped, so it never touches shared state.
        var result = new TaskCompletionSource<IReadOnlyList<BackfillItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result.TrySetResult(ReadOnStaThread(since));
            }
            catch (Exception ex)
            {
                Log.Error("WindowsHistorySource.Read", ex);
                result.TrySetResult(Array.Empty<BackfillItem>());
            }
        })
        {
            IsBackground = true,
            Name = "Waigoma.ClipboardHistory.HistoryRead",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!result.Task.Wait(OverallTimeout))
        {
            Log.Info("Windows clipboard history read timed out.");
            return Array.Empty<BackfillItem>();
        }

        return result.Task.Result;
    }

    private static IReadOnlyList<BackfillItem> ReadOnStaThread(DateTimeOffset since)
    {
        ClipboardHistoryItemsResult? history = Await(Clipboard.GetHistoryItemsAsync());
        if (history is null || history.Status != ClipboardHistoryItemsResultStatus.Success)
        {
            return Array.Empty<BackfillItem>();
        }

        var items = new List<BackfillItem>();
        foreach (ClipboardHistoryItem entry in history.Items)
        {
            // An item that cannot be read is skipped alone, so one odd entry does not hide the rest.
            try
            {
                if (entry.Timestamp <= since)
                {
                    continue;
                }

                DataPackageView content = entry.Content;
                string? text = null;
                IReadOnlyList<string>? files = null;
                if (content.Contains(StandardDataFormats.Text))
                {
                    text = Await(content.GetTextAsync());
                }

                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    files = PathsOf(Await(content.GetStorageItemsAsync()));
                }

                if (string.IsNullOrEmpty(text) && files is not { Count: > 0 })
                {
                    continue;
                }

                items.Add(new BackfillItem(string.IsNullOrEmpty(text) ? null : text, files, entry.Timestamp));
            }
            catch (Exception ex)
            {
                Log.Error("WindowsHistorySource.Item", ex);
            }
        }

        // Oldest first, so the service records them in the order they were copied.
        items.Sort((a, b) => a.CopiedAt.CompareTo(b.CopiedAt));
        return items;
    }

    private static IReadOnlyList<string>? PathsOf(IReadOnlyList<IStorageItem>? storageItems)
    {
        if (storageItems is null)
        {
            return null;
        }

        var paths = new List<string>();
        foreach (IStorageItem item in storageItems)
        {
            string? path = item switch
            {
                StorageFile file => file.Path,
                StorageFolder folder => folder.Path,
                _ => null,
            };
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    // A timed-out await yields null, which the callers treat as "nothing to read".
    private static T? Await<T>(IAsyncOperation<T> operation)
    {
        var task = operation.AsTask();
        return task.Wait(AwaitTimeout) ? task.Result : default;
    }
}
