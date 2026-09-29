using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VCCad.App.Controls;

/// <summary>
/// The "the assistant has control" badge: a spinning pizza and a word, for the corner
/// of the view.
///
/// The editor is locked for the whole of a turn, and until this existed the only sign
/// of it was the Cancel button inside the diagnostics panel - easy to have closed, and
/// silent during a long step while the window sat there looking idle. This is the
/// signal that is always on screen while the model is working, whatever the panel is
/// doing.
///
/// It is one control rather than a bare <see cref="BusySpinner"/> so the badge (the
/// backing pill, the wording and the spinner) turns on and off as a unit, and so the
/// host that knows the busy state has a single thing to drive.
/// </summary>
public sealed class AssistantBusyIndicator : UserControl
{
    private readonly BusySpinner _spinner = new();

    public AssistantBusyIndicator()
    {
        var label = new TextBlock
        {
            Text = "assistant working…",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF2)),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _spinner, label },
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x18, 0x18, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x6E, 0x9E, 0xD8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6, 14, 6),
            Child = row,
        };

        IsHitTestVisible = false;
        IsVisible = false;
    }

    /// <summary>The spinning pizza itself. Exposed so a test can watch it turn.</summary>
    public BusySpinner Spinner => _spinner;

    /// <summary>Whether the assistant currently has control of the document.</summary>
    public bool IsBusy => _spinner.IsSpinning;

    /// <summary>
    /// Shows the badge and starts the pizza, or hides the badge and stops it. This is
    /// the only thing a caller has to call, and it is safe to call with the state it
    /// already has.
    /// </summary>
    public void SetBusy(bool busy)
    {
        _spinner.IsSpinning = busy;
        IsVisible = busy;
    }
}
