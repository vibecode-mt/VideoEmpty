using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoEmpty.Core.Model;

namespace VideoEmpty.UI;

/// <summary>
/// Asks the user how to shift template instances when the replacement video has a
/// different duration than the original. The user enters a timecode (e.g. "10:12" =
/// 10 minutes 12 seconds, or "1:02:03"); a radio button chooses whether existing
/// instances move later or earlier on the new timeline.
/// Returns the signed shift in milliseconds, or null if the user cancels.
/// </summary>
internal sealed class ReplaceVideoShiftDialog : Window
{
    private readonly DurationInput _input = new(allowNegative: false);
    private readonly RadioButton _later;
    private readonly RadioButton _earlier;
    private readonly TextBlock _error;

    public static Task<int?> ShowAsync(
        Window owner,
        string oldFileName,
        int oldDurationMs,
        string newFileName,
        int newDurationMs,
        int instanceCount)
    {
        var dlg = new ReplaceVideoShiftDialog(oldFileName, oldDurationMs, newFileName, newDurationMs, instanceCount);
        return dlg.ShowDialog<int?>(owner);
    }

    private ReplaceVideoShiftDialog(
        string oldFileName, int oldDurationMs,
        string newFileName, int newDurationMs,
        int instanceCount)
    {
        Title = "Replace video — shift instances";
        Width = 540;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                $"Replacing \"{oldFileName}\" ({Timecode.Format(oldDurationMs)}) with " +
                $"\"{newFileName}\" ({Timecode.Format(newDurationMs)}).\n\n" +
                $"Where does the original video sit inside the new one? " +
                $"Enter the offset in the boxes below — all {instanceCount} template instance(s) " +
                "will be shifted by this amount."
        };

        _later = new RadioButton { Content = "Shift later (new video is longer / has extra intro)", IsChecked = true, GroupName = "dir" };
        _earlier = new RadioButton { Content = "Shift earlier (new video is shorter / trimmed intro)", GroupName = "dir" };

        _error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var ok = new Button { Content = "Replace", IsDefault = true, MinWidth = 90 };
        var skip = new Button { Content = "No shift", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };

        ok.Click += (_, _) =>
        {
            var ms = _input.TryGetTotalMs();
            if (ms is null)
            {
                _error.Text = "Fix the highlighted field: minutes/seconds must be 0\u201359, ms 0\u2013999.";
                _error.IsVisible = true;
                return;
            }
            var signed = _earlier.IsChecked == true ? -ms.Value : ms.Value;
            Close((int?)signed);
        };
        skip.Click += (_, _) => Close((int?)0);
        cancel.Click += (_, _) => Close((int?)null);

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, skip, ok }
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        summary,
                        new TextBlock { Text = "Offset:", Margin = new Thickness(0, 6, 0, 0) },
                        _input.Root,
                        _later,
                        _earlier,
                        _error
                    }
                }
            }
        };
    }
}
