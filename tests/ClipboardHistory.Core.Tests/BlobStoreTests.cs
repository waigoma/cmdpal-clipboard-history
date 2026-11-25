using System.Text;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class BlobStoreTests
{
    [Fact]
    public void Save_SameBytesTwice_ReturnsSameName_WritesOneFile_AndLeavesTmpEmpty()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        var blobs = new BlobStore(temp.Paths);
        byte[] data = Encoding.UTF8.GetBytes("<b>same</b>");

        string first = blobs.Save(data, "html");
        string second = blobs.Save(data, "html");

        Assert.Equal(first, second);
        Assert.Equal(ContentHasher.ForBytes(data) + ".html", first);
        Assert.Single(Directory.GetFiles(temp.Paths.Files));
        Assert.Empty(Directory.GetFiles(temp.Paths.Temp));
        Assert.Equal(data, File.ReadAllBytes(blobs.FullPath(first)));
    }

    [Fact]
    public void Save_DifferentBytes_GetDifferentNames()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        var blobs = new BlobStore(temp.Paths);

        string a = blobs.Save(new byte[] { 1 }, "png");
        string b = blobs.Save(new byte[] { 2 }, "png");

        Assert.NotEqual(a, b);
        Assert.Equal(2, Directory.GetFiles(temp.Paths.Files).Length);
    }

    [Fact]
    public void FullPath_PointsInsideFilesFolder()
    {
        using var temp = new TempStore();
        var blobs = new BlobStore(temp.Paths);

        Assert.Equal(temp.Paths.Files, Path.GetDirectoryName(blobs.FullPath("x.png")));
    }
}
