using System.Globalization;

namespace VideoEmpty.Core.Model;

/// <summary>
/// Parses and formats human-friendly timecodes used by the UI when shifting
/// template instances after a video replacement.
/// Accepted forms (whitespace is trimmed; an optional leading '-' negates):
///   <c>S</c>, <c>S.fff</c>, <c>S,fff</c>
///   <c>M:S[.fff]</c>
///   <c>H:M:S[.fff]</c>
/// Fractional seconds may use either '.' or ',' (the SRT-style separator).
/// </summary>
public static class Timecode
{
    /// <summary>Parses a timecode into milliseconds. Throws on invalid input.</summary>
    public static int ParseToMs(string text)
    {
        if (!TryParseToMs(text, out var ms))
            throw new FormatException($"Invalid timecode: '{text}'. Expected [H:]M:S[.fff] or seconds.");
        return ms;
    }

    /// <summary>
    /// Tries to parse a timecode into milliseconds. Returns false on invalid input.
    /// </summary>
    public static bool TryParseToMs(string? text, out int ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var raw = text.Trim();
        var sign = 1;
        if (raw.StartsWith('-')) { sign = -1; raw = raw[1..].TrimStart(); }
        else if (raw.StartsWith('+')) { raw = raw[1..].TrimStart(); }
        if (raw.Length == 0) return false;

        // Normalise comma-as-decimal (e.g. SRT "00:00:10,500").
        raw = raw.Replace(',', '.');

        var parts = raw.Split(':');
        if (parts.Length == 0 || parts.Length > 3) return false;

        double hours = 0, minutes = 0, seconds = 0;
        try
        {
            switch (parts.Length)
            {
                case 1:
                    if (!TryParseNonNegativeDouble(parts[0], out seconds)) return false;
                    break;
                case 2:
                    if (!TryParseNonNegativeInt(parts[0], out var mm)) return false;
                    if (!TryParseNonNegativeDouble(parts[1], out seconds)) return false;
                    if (seconds >= 60) return false;
                    minutes = mm;
                    break;
                case 3:
                    if (!TryParseNonNegativeInt(parts[0], out var hh)) return false;
                    if (!TryParseNonNegativeInt(parts[1], out var mm2)) return false;
                    if (!TryParseNonNegativeDouble(parts[2], out seconds)) return false;
                    if (mm2 >= 60 || seconds >= 60) return false;
                    hours = hh;
                    minutes = mm2;
                    break;
            }
        }
        catch
        {
            return false;
        }

        var total = (hours * 3600.0 + minutes * 60.0 + seconds) * 1000.0;
        if (double.IsNaN(total) || double.IsInfinity(total)) return false;
        ms = sign * (int)Math.Round(total);
        return true;
    }

    /// <summary>Formats milliseconds as <c>H:MM:SS.fff</c> (or <c>MM:SS.fff</c> when under an hour).</summary>
    public static string Format(int ms)
    {
        var negative = ms < 0;
        var abs = Math.Abs(ms);
        var ts = TimeSpan.FromMilliseconds(abs);
        var s = ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        return negative ? "-" + s : s;
    }

    private static bool TryParseNonNegativeInt(string s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 0;

    private static bool TryParseNonNegativeDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0;
}
