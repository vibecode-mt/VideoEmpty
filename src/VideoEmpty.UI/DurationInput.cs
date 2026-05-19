using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VideoEmpty.UI;

/// <summary>
/// A compact input row for an unambiguous signed duration: sign selector + four
/// integer fields (hours, minutes, seconds, milliseconds) + a live preview label.
/// Avoids the <c>:</c>-vs-<c>.</c> ambiguity of free-text timecodes.
/// </summary>
internal sealed class DurationInput
{
    private readonly ComboBox _sign;
    private readonly TextBox _hours;
    private readonly TextBox _minutes;
    private readonly TextBox _seconds;
    private readonly TextBox _millis;
    private readonly TextBlock _preview;

    public Control Root { get; }

    public DurationInput(bool allowNegative = true)
    {
        _sign = new ComboBox
        {
            Width = 60,
            ItemsSource = allowNegative ? new[] { "+", "-" } : new[] { "+" },
            SelectedIndex = 0
        };

        _hours   = MakeField("0", 50);
        _minutes = MakeField("0", 50);
        _seconds = MakeField("0", 50);
        _millis  = MakeField("0", 60);

        _preview = new TextBlock
        {
            Foreground = Brushes.SteelBlue,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _sign,
                _hours,   new TextBlock { Text = "h", VerticalAlignment = VerticalAlignment.Center },
                _minutes, new TextBlock { Text = "m", VerticalAlignment = VerticalAlignment.Center },
                _seconds, new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center },
                _millis,  new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center }
            }
        };

        Root = new StackPanel { Spacing = 2, Children = { row, _preview } };

        foreach (var t in new[] { _hours, _minutes, _seconds, _millis })
            t.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _sign.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private static TextBox MakeField(string text, double width) => new()
    {
        Text = text,
        Width = width,
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    /// <summary>
    /// Returns the signed total in milliseconds, or null if any field is invalid
    /// (non-integer, negative, or m/s ≥ 60).
    /// </summary>
    public int? TryGetTotalMs()
    {
        if (!TryParseInt(_hours.Text, out var h) || h < 0) return null;
        if (!TryParseInt(_minutes.Text, out var m) || m < 0 || m >= 60) return null;
        if (!TryParseInt(_seconds.Text, out var s) || s < 0 || s >= 60) return null;
        if (!TryParseInt(_millis.Text, out var ms) || ms < 0 || ms >= 1000) return null;

        var total = ((long)h * 3_600_000) + ((long)m * 60_000) + ((long)s * 1_000) + ms;
        if (total > int.MaxValue) return null;
        var signed = _sign.SelectedIndex == 1 ? -(int)total : (int)total;
        return signed;
    }

    private void UpdatePreview()
    {
        var ms = TryGetTotalMs();
        if (ms is null)
        {
            _preview.Text = "Invalid — minutes/seconds must be 0–59, ms 0–999.";
            _preview.Foreground = Brushes.IndianRed;
            return;
        }
        var abs = Math.Abs(ms.Value);
        var totalSeconds = abs / 1000.0;
        _preview.Text =
            $"= {(ms.Value < 0 ? "−" : "+")}{abs:N0} ms " +
            $"(= {(ms.Value < 0 ? "−" : "+")}{totalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s)";
        _preview.Foreground = ms.Value == 0 ? Brushes.Gray : Brushes.SteelBlue;
    }

    private static bool TryParseInt(string? s, out int v)
    {
        if (string.IsNullOrWhiteSpace(s)) { v = 0; return true; }
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }
}
