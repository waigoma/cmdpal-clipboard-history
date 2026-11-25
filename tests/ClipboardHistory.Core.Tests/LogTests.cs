using System.IO;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

// Log is process-wide, so each test points it at its own folder and removes that folder afterwards.
public sealed class LogTests : IDisposable
{
    private const int RotationBytes = 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClipboardHistoryTests", "log-" + Guid.NewGuid().ToString("N"));

    public LogTests()
    {
        Directory.CreateDirectory(_dir);
        Log.Initialize(_dir);
    }

    private string LogPath => Path.Combine(_dir, "log.txt");

    private string RotatedPath => Path.Combine(_dir, "log.1.txt");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort, as in TempStore.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Error_WritesTypeAndHResult_ButNeverTheMessage()
    {
        Log.Error("test.where", new InvalidOperationException("secret-message-123"));

        string text = File.ReadAllText(LogPath);
        Assert.Contains("test.where", text);
        Assert.Contains("type=System.InvalidOperationException", text);
        Assert.Contains("hresult=0x", text);
        Assert.DoesNotContain("secret-message-123", text);
    }

    [Fact]
    public void Error_InnerException_ContributesTypeAndHResult_ButNoMessage()
    {
        var inner = new IOException("inner-secret-456");

        Log.Error("test.inner", new InvalidOperationException("outer-secret-789", inner));

        string text = File.ReadAllText(LogPath);
        Assert.Contains("type=System.IO.IOException", text);
        Assert.DoesNotContain("inner-secret-456", text);
        Assert.DoesNotContain("outer-secret-789", text);
    }

    [Fact]
    public void Info_OnceTheLogIsFull_MovesItToLog1_AndStartsANewOne()
    {
        File.WriteAllText(LogPath, new string('x', RotationBytes));

        Log.Info("after-rotation");

        Assert.Equal(RotationBytes, new FileInfo(RotatedPath).Length);
        Assert.Contains("after-rotation", File.ReadAllText(LogPath));
        Assert.True(new FileInfo(LogPath).Length < 1024);
    }

    [Fact]
    public void Info_WhenTheRotationTargetIsLocked_SkipsRotation_AndStillAppends()
    {
        File.WriteAllText(LogPath, new string('x', RotationBytes));
        File.WriteAllText(RotatedPath, "previous");

        using (new FileStream(RotatedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Log.Info("appended-while-locked");
        }

        Assert.Contains("appended-while-locked", File.ReadAllText(LogPath));
        Assert.Equal("previous", File.ReadAllText(RotatedPath));
    }
}
