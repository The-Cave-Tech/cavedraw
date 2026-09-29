using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Units;
using VCCad.Geometry;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views.Panes;

/// <summary>Transform tab: position/size/rotation fields with a 3×3 circle
/// reference-point picker. Point selections show position only.</summary>
public partial class TransformPane : UserControl
{
    private EditorViewModel? _vm;
    private int _pivot = 4;
    private readonly List<Button> _pivotButtons = new();
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52));

    public TransformPane()
    {
        InitializeComponent();
        BuildPivotPicker();
        foreach (TextBox box in new[] { XBox, YBox, WBox, HBox, AngleBox, SkewBox })
        {
            box.LostFocus += (_, _) => CommitFromField(box);
        }
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();
        vm.TransformChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>A 3×3 grid of small circles over cross-hair guide lines.</summary>
    private void BuildPivotPicker()
    {
        var grid = new Grid
        {
            Width = 50,
            Height = 50,
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("*,*,*"),
        };

        // A square the nine marks sit on. A cross-hair through the middle said "nine points in
        // a grid"; a square says "nine points of this object", which is what the reference
        // shows and what the control actually means.
        var square = new Border
        {
            Margin = new Thickness(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)),
            CornerRadius = new CornerRadius(1),
        };
        Grid.SetColumnSpan(square, 3);
        Grid.SetRowSpan(square, 3);
        grid.Children.Add(square);

        for (int i = 0; i < 9; i++)
        {
            int index = i;
            var dot = new Button
            {
                Width = 10,
                Height = 10,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.2),
                Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27)),
                BorderBrush = IdleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(dot, $"Reference point {index + 1}");
            dot.Click += (_, _) => { _pivot = index; Refresh(); };
            Grid.SetColumn(dot, i % 3);
            Grid.SetRow(dot, i / 3);
            grid.Children.Add(dot);
            _pivotButtons.Add(dot);
        }

        PivotHost.Content = grid;
    }

    private void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        for (int i = 0; i < _pivotButtons.Count; i++)
        {
            _pivotButtons[i].BorderBrush = i == _pivot ? ActiveBrush : IdleBrush;
            _pivotButtons[i].Background = i == _pivot
                ? new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E))
                : new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27));
        }

        SetBoxText(XBox, null);
        SetBoxText(YBox, null);
        SetBoxText(WBox, null);
        SetBoxText(HBox, null);
        SetBoxText(AngleBox, null);
        SetBoxText(SkewBox, null);

        // Artboards have a document rectangle; transform them like objects
        // (position + size; rotation not applicable).
        if (_vm.SelectedArtboard is { } artboard)
        {
            SetBoxText(XBox, artboard.X);
            SetBoxText(YBox, artboard.Y);
            SetBoxText(WBox, artboard.Width);
            SetBoxText(HBox, artboard.Height);
            XBox.IsEnabled = YBox.IsEnabled = WBox.IsEnabled = HBox.IsEnabled = true;
            AngleBox.IsEnabled = false;
            foreach (Button dot in _pivotButtons)
            {
                dot.IsEnabled = false;
            }

            return;
        }

        bool pointMode = _vm.HasPointSelection;
        bool objectMode = _vm.HasTransformableSelection;
        if (pointMode && _vm.PointPosition is { } pos)
        {
            SetBoxText(XBox, pos.X);
            SetBoxText(YBox, pos.Y);
        }
        else if (objectMode)
        {
            (Rect2D bounds, double angle) = _vm.TransformReadout();
            if (!bounds.IsEmpty)
            {
                Point2D reference = ReferencePoint(bounds, _pivot);
                SetBoxText(XBox, reference.X);
                SetBoxText(YBox, reference.Y);
                SetBoxText(WBox, bounds.Width);
                SetBoxText(HBox, bounds.Height);
                SetBoxText(AngleBox, Math.Round(angle, 3));
            }
        }

        foreach (Button dot in _pivotButtons)
        {
            dot.IsEnabled = objectMode;
        }

        XBox.IsEnabled = pointMode || objectMode;
        YBox.IsEnabled = pointMode || objectMode;
        WBox.IsEnabled = objectMode;
        HBox.IsEnabled = objectMode;
        AngleBox.IsEnabled = objectMode;

        // Shown, because the reference shows an S field and a person should see the control
        // that will one day be there - but disabled, because the model has no skew and a field
        // that accepts a value and ignores it is worse than one that says it cannot.
        SetBoxText(SkewBox, 0);
        SkewBox.IsEnabled = false;
        ToolTip.SetTip(SkewBox, "Skew is not supported by the model yet");
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            CommitFromField(box);
            e.Handled = true;
        }
    }

    private void CommitFromField(TextBox field)
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.SelectedArtboard is { } artboard)
        {
            Rect2D before = artboard.Bounds;
            double x = TryRead(XBox, out double ax) ? ax : artboard.X;
            double y = TryRead(YBox, out double ay) ? ay : artboard.Y;
            double aw = TryRead(WBox, out double wv) ? wv : artboard.Width;
            double ah = TryRead(HBox, out double hv) ? hv : artboard.Height;
            _vm.ApplyArtboardBounds(artboard, before,
                new Rect2D(x, y, Math.Max(1, aw), Math.Max(1, ah)));
            Refresh();
            return;
        }

        if (_vm.HasPointSelection)
        {
            if (_vm.PointPosition is not { } current)
            {
                return;
            }

            Point2D target = current;
            if (field == XBox && TryRead(XBox, out double x))
            {
                target = new Point2D(x, current.Y);
            }
            else if (field == YBox && TryRead(YBox, out double y))
            {
                target = new Point2D(current.X, y);
            }

            _vm.MovePointTo(target);
            Refresh();
            return;
        }

        if (!_vm.HasTransformableSelection)
        {
            return;
        }

        (Rect2D bounds, double currentAngle) = _vm.TransformReadout();
        if (bounds.IsEmpty)
        {
            return;
        }

        Point2D reference = ReferencePoint(bounds, _pivot);
        Vector2D translation = new();
        double? tx = TryRead(XBox, out double rx) ? rx : null;
        double? ty = TryRead(YBox, out double ry) ? ry : null;
        if (tx.HasValue || ty.HasValue)
        {
            translation = new Point2D(tx ?? reference.X, ty ?? reference.Y) - reference;
        }

        double scaleX = 1, scaleY = 1;
        if (TryRead(WBox, out double w) && bounds.Width > 1e-6)
        {
            scaleX = w / bounds.Width;
        }

        if (TryRead(HBox, out double h) && bounds.Height > 1e-6)
        {
            scaleY = h / bounds.Height;
        }

        double rotationDelta = TryRead(AngleBox, out double angle) ? angle - currentAngle : 0;
        _vm.ApplyTransform(reference, translation, scaleX, scaleY, rotationDelta);
        Refresh();
    }

    /// <summary>
    /// A field's text as a number of document units.
    ///
    /// The text is an EXPRESSION, not a number: "5.5in * 5 / 2" is a length and evaluates to
    /// 349.25 mm, and a bare "5" means five of the configured unit. Reading it with
    /// double.TryParse - which is what this used to do - silently yields 0 for anything with
    /// arithmetic in it, so a person typing a sum would watch the field snap to zero.
    ///
    /// A rotation is an angle rather than a length, so it is read from the same evaluator and
    /// then taken as degrees.
    /// </summary>
    private static bool TryRead(TextBox box, out double value)
    {
        value = 0;
        string text = box.Text?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            return false;
        }

        if (LengthExpression.TryEvaluate(text, UnitSettings.Current.Unit, out Length length, out _))
        {
            // Angles carry a degree sign or none; a length with a unit is still a number of
            // degrees here, because the field says so.
            value = length.Millimetres;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A length in the configured unit, written the way the field shows it.</summary>
    private static string Format(double millimetres)
        => UnitSettings.Current.FormatWithUnit(Length.FromMillimetres(millimetres));

    private static Point2D ReferencePoint(Rect2D bounds, int pivot)
    {
        double x = (pivot % 3) switch { 0 => bounds.Left, 1 => bounds.Center.X, _ => bounds.Right };
        double y = (pivot / 3) switch { 0 => bounds.Top, 1 => bounds.Center.Y, _ => bounds.Bottom };
        return new Point2D(x, y);
    }

    private void SetBoxText(TextBox box, double? value)
    {
        if (box.IsFocused)
        {
            return;
        }

        if (!value.HasValue)
        {
            box.Text = string.Empty;
            return;
        }

        // Positions and sizes are lengths and carry the configured unit. Rotation and skew are
        // angles and carry a degree sign, which is a unit too.
        box.Text = box == AngleBox || box == SkewBox
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "\u00b0"
            : Format(value.Value);
    }
}
