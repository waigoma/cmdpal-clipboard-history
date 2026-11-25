using System.Text;

namespace Waigoma.ClipboardHistory.Worker;

public static class OcrText
{
    // OCR puts a space between every glyph of Japanese text, which search and display do not want.
    // Newlines are kept because they carry layout that the reader still needs.
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsRunChar(text[i]))
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && IsRunChar(text[i]))
            {
                i++;
            }

            bool betweenJapanese = start > 0 && i < text.Length
                && IsJapanese(text[start - 1]) && IsJapanese(text[i]);
            if (!betweenJapanese)
            {
                sb.Append(text, start, i - start);
            }
        }

        return sb.ToString();
    }

    private static bool IsRunChar(char c) => c == ' ' || c == '\t';

    private static bool IsJapanese(char c) =>
        (c >= '　' && c <= 'ヿ')
        || (c >= '㐀' && c <= '鿿')
        || (c >= '豈' && c <= '﫿')
        || (c >= '＀' && c <= '￯');
}
