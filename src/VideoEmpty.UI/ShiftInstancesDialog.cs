using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;

namespace VideoEmpty.UI;

/// <summary>
/// Bulk-shifts the StartMs of template instances. Inputs are split into discrete
/// hours/minutes/seconds/milliseconds fields so there is no ambiguity about whether
/// <c>58</c> means seconds or minutes.
/// Result tuple = (shiftMs, scope). Returns null when the user cancels.
/// </summary>
internal sealed class ShiftInstancesDialog : Window
{
    private readonly DurationInput _input = new(allowNegative: true);
    private readonly RadioButton _all;
    private readonly RadioButton _before;
    private readonly RadioButton _after;
    private readonly RadioButton _atOrBefore;
    private readonly RadioButton _atOrAfter;
    private readonly RadioButton _only;
    private readonly TextBlock _error;
    private readonly bool _hasReference;

    public static Task<(int ShiftMs, InstanceShiftScope Scope)?> ShowAsync(
        Window owner,
        TemplateInstance? referenceInstance,
        string? referenceTemplateName)
    {
        var dlg = new ShiftInstancesDialog(referenceInstance, referenceTemplateName);
        return dlg.ShowDialog<(int, InstanceShiftScope)?>(owner);
    }

    private ShiftInstancesDialog(TemplateInstance? referenceInstance, string? referenceTemplateName)
    {
        Title = "Shift instance times";
        Width = 560;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _hasReference = referenceInstance is not null;

        var refLabel = _hasReference
            ? $"Selected instance: \"{referenceTemplateName}\" @ {Timecode.Format(referenceInstance!.StartMs)}"
            : "No instance selected — only the \u201CAll instances\u201D scope is available. Select an instance in the right panel to enable the other scopes.";

        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Shift the StartMs of template instances by the amount below. " +
                "Use the sign selector to move earlier (\u2212) or later (+).\n\n" +
                refLabel
        };

        _all        = new RadioButton { Content = "All instances",                                       GroupName = "scope", IsChecked = true };
        _before     = new RadioButton { Content = "Instances strictly before the selected one",          GroupName = "scope", IsEnabled = _hasReference };
        _atOrBefore = new RadioButton { Content = "Instances at or before the selected one (inclusive)", GroupName = "scope", IsEnabled = _hasReference };
        _after      = new RadioButton { Content = "Instances strictly after the selected one",           GroupName = "scope", IsEnabled = _hasReference };
        _atOrAfter  = new RadioButton { Content = "Instances at or after the selected one (inclusive)",  GroupName = "scope", IsEnabled = _hasReference };
        _only       = new RadioButton { Content = "Only the selected instance",                          GroupName = "scope", IsEnabled = _hasReference };

        _error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var ok     = new Button { Content = "Shift", IsDefault = true, MinWidth = 90 };
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
            if (ms.Value == 0)
            {
                _error.Text = "Enter a non-zero shift.";
                _error.IsVisible = true;
                return;
            }
            Close(((int, InstanceShiftScope)?)(ms.Value, SelectedScope()));
        };
        cancel.Click += (_, _) => Close(((int, InstanceShiftScope)?)null);

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
                    Children = { cancel, ok }
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        header,
                        new TextBlock { Text = "Shift by:", Margin = new Thickness(0, 6, 0, 0) },
                        _input.Root,
                        new TextBlock { Text = "Scope:", Margin = new Thickness(0, 6, 0, 0) },
                        _all, _before, _atOrBefore, _after, _atOrAfter, _only,
                        _error
                    }
                }
            }
        };
    }

    private InstanceShiftScope SelectedScope()
    {
        if (_before.IsChecked == true)     return InstanceShiftScope.Before;
        if (_atOrBefore.IsChecked == true) return InstanceShiftScope.AtOrBefore;
        if (_after.IsChecked == true)      return InstanceShiftScope.After;
        if (_atOrAfter.IsChecked == true)  return InstanceShiftScope.AtOrAfter;
        if (_only.IsChecked == true)       return InstanceShiftScope.OnlyReference;
        return InstanceShiftScope.All;
    }
}
