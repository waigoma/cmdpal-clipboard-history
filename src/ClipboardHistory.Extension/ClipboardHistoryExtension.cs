using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace Waigoma.ClipboardHistory.Extension;

[Guid("7C1B6F3E-5A0D-4E0B-9B52-2F6A4C8D9E17")]
public sealed partial class ClipboardHistoryExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly AppHost? _host;
    private readonly HistoryCommandsProvider _provider;

    public ClipboardHistoryExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;

        // The host is built here, not in Program, so a failure is logged and the palette still shows its entry.
        try
        {
            _host = new AppHost();
        }
        catch (Exception ex)
        {
            Log.Error("ClipboardHistoryExtension.ctor", ex);
        }

        _provider = new HistoryCommandsProvider(_host);
    }

    public object? GetProvider(ProviderType providerType)
    {
        try
        {
            return providerType == ProviderType.Commands ? _provider : null;
        }
        catch (Exception ex)
        {
            Log.Error("ClipboardHistoryExtension.GetProvider", ex);
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("ClipboardHistoryExtension.Dispose", ex);
        }
        finally
        {
            _extensionDisposedEvent.Set();
        }
    }
}
