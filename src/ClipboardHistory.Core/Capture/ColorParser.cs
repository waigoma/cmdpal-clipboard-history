using System.Globalization;
using System.Text.RegularExpressions;

namespace Waigoma.ClipboardHistory.Capture;

public readonly record struct Rgba(byte R, byte G, byte B, byte A);

public static partial class ColorParser
{
    // Patterns use [0-9] rather than \d: \d also matches Unicode digits, which the number parsing does not expect.
    private const RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Longer numbers are not colour values; the limit keeps parsing cost and overflow out of reach.
    private const int MaxNumberLength = 12;

    public static bool TryParse(string text, out Rgba color)
    {
        color = default;
        if (text.Length > ContentClassifier.MaxScannedLength)
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith('#'))
        {
            return TryParseHex(trimmed.AsSpan(1), out color);
        }

        Match match = RgbCommaPattern().Match(trimmed);
        if (match.Success)
        {
            return TryBuildRgb(match, commaForm: true, out color);
        }

        match = RgbSpacePattern().Match(trimmed);
        if (match.Success)
        {
            return TryBuildRgb(match, commaForm: false, out color);
        }

        match = HslCommaPattern().Match(trimmed);
        if (match.Success)
        {
            return TryBuildHsl(match, commaForm: true, out color);
        }

        match = HslSpacePattern().Match(trimmed);
        if (match.Success)
        {
            return TryBuildHsl(match, commaForm: false, out color);
        }

        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> digits, out Rgba color)
    {
        color = default;
        if (digits.Length is not (3 or 6 or 8))
        {
            return false;
        }

        if (digits.Length == 3)
        {
            if (!TryHexDigit(digits[0], out int r) || !TryHexDigit(digits[1], out int g) || !TryHexDigit(digits[2], out int b))
            {
                return false;
            }

            // #RGB expands each nibble to the same byte, so #f00 is #ff0000.
            color = new Rgba((byte)(r * 17), (byte)(g * 17), (byte)(b * 17), 255);
            return true;
        }

        if (!TryHexByte(digits.Slice(0, 2), out byte red)
            || !TryHexByte(digits.Slice(2, 2), out byte green)
            || !TryHexByte(digits.Slice(4, 2), out byte blue))
        {
            return false;
        }

        byte alpha = 255;
        if (digits.Length == 8 && !TryHexByte(digits.Slice(6, 2), out alpha))
        {
            return false;
        }

        color = new Rgba(red, green, blue, alpha);
        return true;
    }

    private static bool TryHexByte(ReadOnlySpan<char> pair, out byte value)
    {
        value = 0;
        if (!TryHexDigit(pair[0], out int high) || !TryHexDigit(pair[1], out int low))
        {
            return false;
        }

        value = (byte)((high << 4) | low);
        return true;
    }

    private static bool TryHexDigit(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F')
        {
            value = (char.ToLowerInvariant(c) - 'a') + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryBuildRgb(Match match, bool commaForm, out Rgba color)
    {
        color = default;
        bool isRgba = string.Equals(match.Groups["fn"].Value, "rgba", StringComparison.OrdinalIgnoreCase);
        bool hasAlpha = match.Groups["a"].Success;

        // The comma form ties the function name to its arity; the space form takes an optional "/ alpha" for either name.
        if (commaForm && isRgba != hasAlpha)
        {
            return false;
        }

        if (!TryParseChannel(match.Groups["r"].Value, out byte r)
            || !TryParseChannel(match.Groups["g"].Value, out byte g)
            || !TryParseChannel(match.Groups["b"].Value, out byte b))
        {
            return false;
        }

        byte alpha = 255;
        if (hasAlpha && !TryParseAlpha(match, out alpha))
        {
            return false;
        }

        color = new Rgba(r, g, b, alpha);
        return true;
    }

    private static bool TryBuildHsl(Match match, bool commaForm, out Rgba color)
    {
        color = default;
        bool isHsla = string.Equals(match.Groups["fn"].Value, "hsla", StringComparison.OrdinalIgnoreCase);
        bool hasAlpha = match.Groups["a"].Success;

        if (commaForm && isHsla != hasAlpha)
        {
            return false;
        }

        if (!TryParseNumber(match.Groups["s"].Value, out double saturation)
            || !TryParseNumber(match.Groups["l"].Value, out double lightness)
            || saturation > 100
            || lightness > 100)
        {
            return false;
        }

        byte alpha = 255;
        if (hasAlpha && !TryParseAlpha(match, out alpha))
        {
            return false;
        }

        if (!TryParseNumber(match.Groups["h"].Value, out double hue))
        {
            return false;
        }

        color = FromHsl(hue, saturation / 100, lightness / 100, alpha);
        return true;
    }

    private static bool TryParseChannel(string digits, out byte value)
    {
        value = 0;
        int number = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (number > 255)
        {
            return false;
        }

        value = (byte)number;
        return true;
    }

    private static bool TryParseAlpha(Match match, out byte alpha)
    {
        alpha = 0;
        if (!TryParseNumber(match.Groups["a"].Value, out double value))
        {
            return false;
        }

        if (match.Groups["p"].Success)
        {
            value /= 100;
        }

        if (value < 0 || value > 1)
        {
            return false;
        }

        alpha = (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);
        return true;
    }

    // Length is capped before parsing, so a long digit run cannot overflow to infinity; the finiteness check covers
    // what the length cap lets through, and a value that is not finite is not a colour.
    private static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        if (text.Length > MaxNumberLength)
        {
            return false;
        }

        value = double.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return double.IsFinite(value);
    }

    // Standard HSL-to-RGB conversion; hue is in degrees and wraps around.
    private static Rgba FromHsl(double hueDegrees, double saturation, double lightness, byte alpha)
    {
        double hue = ((hueDegrees % 360) + 360) % 360 / 360;
        if (saturation == 0)
        {
            byte gray = ToByte(lightness);
            return new Rgba(gray, gray, gray, alpha);
        }

        double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        double p = 2 * lightness - q;
        return new Rgba(
            ToByte(HueToChannel(p, q, hue + 1.0 / 3)),
            ToByte(HueToChannel(p, q, hue)),
            ToByte(HueToChannel(p, q, hue - 1.0 / 3)),
            alpha);
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 0.5)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + (q - p) * (2.0 / 3 - t) * 6;
        }

        return p;
    }

    private static byte ToByte(double unit) =>
        (byte)Math.Clamp(Math.Round(unit * 255, MidpointRounding.AwayFromZero), 0, 255);

    [GeneratedRegex(@"^(?<fn>rgba?)\(\s*(?<r>[0-9]{1,3})\s*,\s*(?<g>[0-9]{1,3})\s*,\s*(?<b>[0-9]{1,3})\s*(?:,\s*(?<a>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<p>%)?\s*)?\)\z", PatternOptions)]
    private static partial Regex RgbCommaPattern();

    [GeneratedRegex(@"^(?<fn>rgba?)\(\s*(?<r>[0-9]{1,3})\s+(?<g>[0-9]{1,3})\s+(?<b>[0-9]{1,3})\s*(?:/\s*(?<a>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<p>%)?\s*)?\)\z", PatternOptions)]
    private static partial Regex RgbSpacePattern();

    [GeneratedRegex(@"^(?<fn>hsla?)\(\s*(?<h>[0-9]+(?:\.[0-9]+)?)(?:deg)?\s*,\s*(?<s>[0-9]+(?:\.[0-9]+)?)%\s*,\s*(?<l>[0-9]+(?:\.[0-9]+)?)%\s*(?:,\s*(?<a>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<p>%)?\s*)?\)\z", PatternOptions)]
    private static partial Regex HslCommaPattern();

    [GeneratedRegex(@"^(?<fn>hsla?)\(\s*(?<h>[0-9]+(?:\.[0-9]+)?)(?:deg)?\s+(?<s>[0-9]+(?:\.[0-9]+)?)%\s+(?<l>[0-9]+(?:\.[0-9]+)?)%\s*(?:/\s*(?<a>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<p>%)?\s*)?\)\z", PatternOptions)]
    private static partial Regex HslSpacePattern();
}
