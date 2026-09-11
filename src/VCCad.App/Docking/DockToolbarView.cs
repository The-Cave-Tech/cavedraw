using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VCCad.App.Docking;

/// <summary>
/// Chrome for a dockable toolbar: a double-line drag handle plus the toolbar
/// content. The content is oriented horizontally when docked top/bottom and
/// vertically when docked left/right.
/// </summary>
public sealed class DockToolbarView : Border
{
    /// <summary>Raised when the user starts dragging this toolbar by its handle.</summary>
    public event EventHandler<string>? DragRequested;

    private readonly ToolbarModel _toolbar;
    private readonly ContentControl _content = new();

    public DockToolbarView(ToolbarModel toolbar)
    {
        _toolbar = toolbar;

        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        BorderThickness = new Thickness(0);
        Margin = new Thickness(6);
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;

        bool vertical = toolbar.Side is DockSide.Left or DockSide.Right;
        var root = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = 4,
        };

        root.Children.Add(BuildDragHandle(vertical));
        _content.Content = toolbar.ContentFactory();
        root.Children.Add(_content);
        Child = root;
    }

    private Control BuildDragHandle(bool vertical)
    {
        var lines = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (vertical)
        {
            lines.Children.Add(new Border { Height = 2, Width = 15, Background = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x78)), CornerRadius = new CornerRadius(1) });
            lines.Children.Add(new Border { Height = 2, Width = 15, Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)), CornerRadius = new CornerRadius(1) });
        }
        else
        {
            lines.Children.Add(new Border { Width = 2, Height = 15, Background = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x78)), CornerRadius = new CornerRadius(1) });
            lines.Children.Add(new Border { Width = 2, Height = 15, Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)), CornerRadius = new CornerRadius(1) });
        }

        var handle = new Border
        {
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            Child = lines,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        handle.PointerPressed += (_, e) =>
        {
            DragRequested?.Invoke(this, _toolbar.Id);
            e.Handled = true;
        };
        return handle;
    }
}
