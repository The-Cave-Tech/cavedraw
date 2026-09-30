using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views;

/// <summary>
/// The shape tool as a compound button: one slot on the toolbar with the nine shapes behind it.
///
/// The button **is** the current shape - it draws the shape itself, from the same library the tool draws
/// with, so the armed shape is visible rather than remembered, and a new shape gets an icon for free
/// because there is no icon to draw. A small bevel in the corner says there is more than one tool behind
/// it; without that the button is indistinguishable from a plain one and the flyout may as well not exist.
///
/// The gesture follows a menu bar's dropdown, because that is the behaviour every person already knows:
///
/// - a **long press** opens the flyout; a short click just picks the button's current shape, so repeated
///   use of the same shape stays one press;
/// - with the flyout open, **dragging** highlights an entry and **releasing** over one chooses it;
/// - **dragging off both** the button and the flyout closes it and chooses nothing - sliding off a menu is
///   how a person cancels;
/// - **releasing without moving off the button** leaves the flyout **open**, waiting for a click: inside
///   chooses, anywhere outside dismisses. The press that opened it was a long press, not a choice, and
///   reading its release as one would make the flyout unusable;
/// - `Escape` closes it, and so does a click outside.
/// </summary>
public sealed class ShapeFlyoutButton : UserControl
{
    /// <summary>How long the button must be held before the flyout opens, in milliseconds.</summary>
    public const int LongPressMilliseconds = 350;

    /// <summary>How many rows the flyout lays out in, so nine shapes do not become a tall strip.</summary>
    private const int FlyoutColumns = 3;

    private readonly Button _button;
    private readonly Grid _glyph;
    private readonly ShapePath _mark;
    private readonly ShapePath _bevel;
    private readonly Border _flyout;
    private readonly Popup _popup;
    private readonly StackPanel _entries;
    private readonly DispatcherTimer _timer;
    private readonly List<(ShapeKind Kind, Button Button)> _entryButtons = new();

    private EditorViewModel? _viewModel;
    private bool _pressed;
    private bool _openedDuringPress;
    private Point _pressPosition;

    public ShapeFlyoutButton()
    {
        Name = "ShapeToolButton";

        _mark = new ShapePath
        {
            Name = "ShapeToolIcon",
            Stroke = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC)),
            StrokeThickness = 1.4,
            Fill = null,
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
        };

        // The corner bevel: small, in the corner, and the only thing that says "there are more of these".
        _bevel = new ShapePath
        {
            Name = "ShapeToolBevel",
            Data = Avalonia.Media.Geometry.Parse("M 0 6 L 6 6 L 6 0 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x96)),
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 1),
        };

        _glyph = new Grid { Width = 22, Height = 22 };
        _glyph.Children.Add(_mark);
        _glyph.Children.Add(_bevel);

        _button = new Button
        {
            Name = "ShapeToolButtonFace",
            Content = _glyph,
            MinWidth = 30,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };

        ToolTip.SetTip(_button, "Shapes: click to draw, hold for the rest");

        _button.PointerPressed += OnPressed;
        _button.PointerMoved += OnMoved;
        _button.PointerReleased += OnReleased;

        _entries = new StackPanel
        {
            Name = "ShapeFlyoutEntries",
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Margin = new Thickness(4),
        };

        _flyout = new Border
        {
            Name = "ShapeFlyout",
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x56)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _entries,
            IsVisible = false,
        };

        _popup = new Popup
        {
            Name = "ShapeFlyoutPopup",
            PlacementTarget = _button,
            Placement = PlacementMode.Right,
            // Light dismiss is the menu-bar rule for a click anywhere outside: it closes the flyout and
            // the click does not also reach the canvas behind it.
            IsLightDismissEnabled = true,
            Child = _flyout,
        };

        var host = new Grid();
        host.Children.Add(_button);
        host.Children.Add(_popup);

        Content = host;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PressMilliseconds) };
        _timer.Tick += (_, _) => OpenFlyout();

        BuildEntries();
    }

    /// <summary>
    /// How long a press must last to open the flyout. Settable so a test can shorten it - the alternative
    /// is a test that sleeps for the real interval, which is slow and flaky in equal measure.
    /// </summary>
    internal int PressMilliseconds { get; set; } = LongPressMilliseconds;

    /// <summary>How many presses and releases the button has handled, for tests to see the events arrive.</summary>
    public int PressCount { get; private set; }

    public int ReleaseCount { get; private set; }

    /// <summary>Whether the flyout is showing. The one piece of state the gesture rules are about.</summary>
    public bool IsFlyoutOpen => _flyout.IsVisible;

    /// <summary>Whether the button carries the corner bevel - the affordance, assertable without a picture.</summary>
    public bool HasBevel => _bevel.IsVisible;

    /// <summary>The flyout's buttons, for a driver or a test: they live in a popup, not this tree.</summary>
    public IReadOnlyList<Button> Entries => _entryButtons.Select(e => e.Button).ToArray();

    /// <summary>The nine shapes, in the order the flyout lists them.</summary>
    public IReadOnlyList<ShapeKind> Shapes => _entryButtons.Select(e => e.Kind).ToArray();

    /// <summary>The chosen entry's name, or empty when nothing is highlighted.</summary>
    public string Highlighted { get; private set; } = string.Empty;

    public void Attach(EditorViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.CurrentShape))
            {
                Redraw();
            }
        };

        Redraw();
    }

    /// <summary>The button's face, for a person or a driver: press it to use the current shape.</summary>
    public Button Face => _button;

    /// <summary>Opens the flyout - what the long press does, and what a driver can do directly.</summary>
    public void OpenFlyout()
    {
        _timer.Stop();
        SetOpen(true);
    }

    /// <summary>Closes the flyout and forgets the highlight.</summary>
    public void CloseFlyout()
    {
        _timer.Stop();
        SetOpen(false);
    }

    /// <summary>Chooses a shape: the button becomes it and the tool is armed.</summary>
    public void Choose(ShapeKind kind)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.CurrentShape = kind;
        _viewModel.Tool = EditorTool.Shape;
        CloseFlyout();
        Redraw();
    }

    /// <summary>Chooses whatever is under the pointer, if anything - the release half of a drag.</summary>
    public bool ChooseHighlighted()
    {
        (ShapeKind Kind, Button Button)? entry = _entryButtons
            .Cast<(ShapeKind Kind, Button Button)?>()
            .FirstOrDefault(e => e!.Value.Button.Name == Highlighted);

        if (entry is null)
        {
            return false;
        }

        Choose(entry.Value.Kind);
        return true;
    }

    /// <summary>Whether a point in this control's space is over the button or the open flyout.</summary>
    public bool ContainsPoint(Point point)
    {
        if (_button.Bounds.Contains(point))
        {
            return true;
        }

        if (!IsFlyoutOpen)
        {
            return false;
        }

        Point inFlyout = this.TranslatePoint(point, _flyout) ?? new Point(-1, -1);
        return _flyout.Bounds.Contains(inFlyout);
    }

    // ---- the gesture ----------------------------------------------------

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        PressCount++;
        _pressed = true;
        _openedDuringPress = false;
        _pressPosition = e.GetPosition(this);
        _timer.Interval = TimeSpan.FromMilliseconds(PressMilliseconds);
        _timer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        Point at = e.GetPosition(this);

        // Held long enough to be a long press but not yet opened: nothing else happens on the way.
        if (!IsFlyoutOpen)
        {
            return;
        }

        // Dragging off both the button and the flyout cancels: sliding off a menu is how a person says no.
        if (!ContainsPoint(at))
        {
            CloseFlyout();
            _openedDuringPress = false;
            _pressed = false;
            return;
        }

        Highlighted = EntryAt(at)?.Name ?? string.Empty;
        HighlightEntries();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        ReleaseCount++;
        bool opened = _openedDuringPress;
        _timer.Stop();
        _pressed = false;

        if (!opened)
        {
            // A short click: use the shape that is already armed, which is what makes repeated use fast.
            if (_viewModel is not null)
            {
                _viewModel.Tool = EditorTool.Shape;
            }

            return;
        }

        // The flyout was opened by this same press. Releasing over an entry chooses it; releasing over the
        // button - without having moved off - leaves the flyout open for a click, because the press that
        // opened it was not a choice.
        if (ChooseHighlighted())
        {
            return;
        }

        if (!_button.Bounds.Contains(e.GetPosition(this)))
        {
            CloseFlyout();
        }
    }

    private void SetOpen(bool open)
    {
        // The Border alone is inside a Popup, so it has to be the Popup that opens - setting the border
        // visible inside a closed popup shows nothing at all.
        _flyout.IsVisible = open;
        _popup.IsOpen = open;
        _openedDuringPress = open && _pressed;

        if (open)
        {
            Highlighted = string.Empty;
            HighlightEntries();
        }
    }

    private Button? EntryAt(Point point)
    {
        foreach ((ShapeKind _, Button button) in _entryButtons)
        {
            Point inEntry = this.TranslatePoint(point, button) ?? new Point(-1, -1);
            if (button.Bounds.Contains(inEntry))
            {
                return button;
            }
        }

        return null;
    }

    private void HighlightEntries()
    {
        foreach ((ShapeKind kind, Button button) in _entryButtons)
        {
            bool on = button.Name == Highlighted;
            button.Background = new SolidColorBrush(on
                ? Color.FromRgb(0x3A, 0x5A, 0x8A)
                : Color.FromRgb(0x2A, 0x2A, 0x32));
        }
    }

    // ---- the flyout's contents ------------------------------------------

    private void BuildEntries()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        for (int i = 0; i < ShapeLibrary.All.Count; i++)
        {
            if (i > 0 && i % FlyoutColumns == 0)
            {
                _entries.Children.Add(row);
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            }

            ShapeKind kind = ShapeLibrary.All[i];
            string name = ShapeLibrary.Name(kind);

            var entry = new Button
            {
                Name = $"ShapeFlyout{char.ToUpperInvariant(name[0])}{name[1..]}",
                Content = name,
                MinWidth = 84,
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC)),
                Padding = new Thickness(8, 3),
            };

            ToolTip.SetTip(entry, name);
            entry.Click += (_, _) => Choose(kind);

            _entryButtons.Add((kind, entry));
            row.Children.Add(entry);
        }

        _entries.Children.Add(row);
    }

    /// <summary>Redraws the button's face from the current shape, so the icon cannot drift from the tool.</summary>
    private void Redraw()
    {
        if (_viewModel is null)
        {
            return;
        }

        _mark.Data = Avalonia.Media.Geometry.Parse(OutlineOf(_viewModel.CurrentShape));
        ToolTip.SetTip(_button, $"Shapes: {ShapeLibrary.Name(_viewModel.CurrentShape)} (hold for the rest)");

        foreach ((ShapeKind kind, Button button) in _entryButtons)
        {
            button.FontWeight = kind == _viewModel.CurrentShape
                ? FontWeight.Bold
                : FontWeight.Normal;
        }
    }

    /// <summary>
    /// The shape's own outline, flattened, as a path scaled into the icon box. The library is the source
    /// of truth for what a shape looks like, so the icon and the drawing can never disagree.
    /// </summary>
    private static string OutlineOf(ShapeKind kind)
    {
        PathItem shape = ShapeLibrary.Create(kind, new ShapeParameters
        {
            Centre = new Point2D(50, 50),
            Width = 100,
            Height = 100,
        });

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(shape);
        if (outlines.Count == 0)
        {
            return "M 0 0";
        }

        Rect2D box = shape.BoundingBox();
        double scale = 100.0 / Math.Max(Math.Max(box.Width, box.Height), 1e-6);

        var text = new System.Text.StringBuilder();
        foreach (FlattenedOutline outline in outlines)
        {
            for (int i = 0; i < outline.Points.Count; i++)
            {
                Point2D point = outline.Points[i];
                double x = (point.X - box.X) * scale;
                double y = (point.Y - box.Y) * scale;
                text.Append(i == 0 ? $"M {x:0.##} {y:0.##}" : $" L {x:0.##} {y:0.##}");
            }

            text.Append(" Z");
        }

        return text.ToString();
    }
}
