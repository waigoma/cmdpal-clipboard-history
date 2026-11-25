using System.Text;
using Waigoma.ClipboardHistory.Interop;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HtmlFormatTests
{
    // Fixed-width offsets keep the header length constant, so the offsets can be written in one pass.
    private const string HeaderTemplate =
        "Version:0.9\r\n" +
        "StartHTML:0000000000\r\n" +
        "EndHTML:0000000000\r\n" +
        "StartFragment:0000000000\r\n" +
        "EndFragment:0000000000\r\n";

    private const string Prefix = "<html><body><!--StartFragment-->";
    private const string Suffix = "<!--EndFragment--></body></html>";

    [Fact]
    public void ExtractFragment_ReturnsFragmentText_FromValidCfHtml()
    {
        byte[] cfHtml = BuildCfHtml("<b>フラグメント</b>");

        string? fragment = HtmlFormat.ExtractFragment(cfHtml);

        Assert.Equal("<b>フラグメント</b>", fragment);
    }

    [Fact]
    public void ExtractFragment_EndFragmentBeyondData_ReturnsNull()
    {
        byte[] cfHtml = BuildCfHtml("abc");
        string text = Encoding.ASCII.GetString(cfHtml);
        string end = "EndFragment:" + ReadOffset(cfHtml, "EndFragment:");
        byte[] broken = Encoding.ASCII.GetBytes(text.Replace(end, "EndFragment:" + (cfHtml.Length + 100).ToString("D10")));

        Assert.Null(HtmlFormat.ExtractFragment(broken));
    }

    [Fact]
    public void ExtractFragment_StartAfterEnd_ReturnsNull()
    {
        byte[] cfHtml = BuildCfHtml("abc");
        string text = Encoding.ASCII.GetString(cfHtml);
        string start = "StartFragment:" + ReadOffset(cfHtml, "StartFragment:");
        string end = "EndFragment:" + ReadOffset(cfHtml, "EndFragment:");
        byte[] broken = Encoding.ASCII.GetBytes(text
            .Replace(start, "StartFragment:0000009999")
            .Replace(end, "EndFragment:0000000001"));

        Assert.Null(HtmlFormat.ExtractFragment(broken));
    }

    [Fact]
    public void ExtractFragment_WithoutHeader_ReturnsNull()
    {
        Assert.Null(HtmlFormat.ExtractFragment(Encoding.UTF8.GetBytes("<html>plain</html>")));
    }

    private static byte[] BuildCfHtml(string fragment)
    {
        byte[] header = Encoding.ASCII.GetBytes(HeaderTemplate);
        byte[] fragmentBytes = Encoding.UTF8.GetBytes(fragment);
        byte[] suffix = Encoding.UTF8.GetBytes(Suffix);
        byte[] prefix = Encoding.UTF8.GetBytes(Prefix);

        int startFragment = header.Length + prefix.Length;
        int endFragment = startFragment + fragmentBytes.Length;
        int endHtml = endFragment + suffix.Length;

        string filled = HeaderTemplate
            .Replace("StartHTML:0000000000", "StartHTML:" + header.Length.ToString("D10"))
            .Replace("EndHTML:0000000000", "EndHTML:" + endHtml.ToString("D10"))
            .Replace("StartFragment:0000000000", "StartFragment:" + startFragment.ToString("D10"))
            .Replace("EndFragment:0000000000", "EndFragment:" + endFragment.ToString("D10"));

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(filled));
        bytes.AddRange(prefix);
        bytes.AddRange(fragmentBytes);
        bytes.AddRange(suffix);
        return bytes.ToArray();
    }

    private static string ReadOffset(byte[] cfHtml, string key)
    {
        string text = Encoding.ASCII.GetString(cfHtml);
        int at = text.IndexOf(key, StringComparison.Ordinal) + key.Length;
        return text.Substring(at, 10);
    }
}
