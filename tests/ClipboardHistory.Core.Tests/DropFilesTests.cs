using System.Text;
using Waigoma.ClipboardHistory.Interop;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class DropFilesTests
{
    [Fact]
    public void RoundTrip_KeepsJapaneseAndSpacesInTwoPaths()
    {
        string[] paths = { @"C:\作業\日本語 フォルダ\メモ.txt", @"D:\a b\c.png" };

        byte[] data = DropFiles.Encode(paths);
        IReadOnlyList<string> decoded = DropFiles.Decode(data);

        Assert.Equal(paths, decoded);
    }

    [Fact]
    public void Encode_WritesWideHeaderWithFileListOffset20()
    {
        byte[] data = DropFiles.Encode(new[] { @"C:\a.txt" });

        Assert.Equal(20u, BitConverter.ToUInt32(data, 0));
        Assert.NotEqual(0u, BitConverter.ToUInt32(data, 16));
    }

    [Fact]
    public void Decode_AnsiListWithFWideZero_ReadsPaths()
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(20u));
        data.AddRange(new byte[8]);
        data.AddRange(BitConverter.GetBytes(0u));
        data.AddRange(BitConverter.GetBytes(0u));
        data.AddRange(Encoding.ASCII.GetBytes(@"C:\a.txt" + "\0" + @"D:\b.txt" + "\0\0"));

        IReadOnlyList<string> decoded = DropFiles.Decode(data.ToArray());

        Assert.Equal(new[] { @"C:\a.txt", @"D:\b.txt" }, decoded);
    }

    [Fact]
    public void Decode_TruncatedData_ReturnsEmpty()
    {
        byte[] data = DropFiles.Encode(new[] { @"C:\a.txt", @"C:\b.txt" });
        byte[] truncated = data[..^2];

        Assert.Empty(DropFiles.Decode(truncated));
    }

    [Fact]
    public void Decode_ShorterThanHeader_ReturnsEmpty()
    {
        Assert.Empty(DropFiles.Decode(new byte[10]));
    }

    [Fact]
    public void Decode_OffsetBelowHeaderSize_ReturnsEmpty()
    {
        byte[] data = DropFiles.Encode(new[] { @"C:\a.txt" });
        BitConverter.GetBytes(4u).CopyTo(data, 0);

        Assert.Empty(DropFiles.Decode(data));
    }

    [Fact]
    public void Decode_OffsetBeyondData_ReturnsEmpty()
    {
        byte[] data = DropFiles.Encode(new[] { @"C:\a.txt" });
        BitConverter.GetBytes((uint)data.Length + 1).CopyTo(data, 0);

        Assert.Empty(DropFiles.Decode(data));
    }

    [Fact]
    public void Decode_OddByteCount_IgnoresTheStrayByte()
    {
        byte[] complete = DropFiles.Encode(new[] { @"C:\a.txt", @"C:\b.txt" });
        byte[] withStray = complete.Append((byte)0x41).ToArray();

        Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, DropFiles.Decode(withStray));
    }

    [Fact]
    public void Decode_TruncatedAnsiList_ReturnsEmpty()
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(20u));
        data.AddRange(new byte[8]);
        data.AddRange(BitConverter.GetBytes(0u));
        data.AddRange(BitConverter.GetBytes(0u));
        data.AddRange(Encoding.ASCII.GetBytes(@"C:\a.txt" + "\0" + @"D:\b.txt" + "\0"));

        Assert.Empty(DropFiles.Decode(data.ToArray()));
    }
}
