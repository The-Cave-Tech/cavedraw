using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
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
        foreach (TextBox box in new[] { XBox, YBox, WBox, HBox, AngleBox })
        {
            box.LostFocus += (_, _) => CommitFromField(box);
        }
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.TransformChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>A 3×3 grid of small circles over cross-hair guide lines.</summary>
    private void BuildPivotPicker()
    {
        var grid = new Grid
        {
            Width = 66,
            Height = 66,
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("*,*,*"),
        };

        // Guide lines through the centre.
        var horizontal = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var vertical = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumnSpan(horizontal, 3);
        Grid.SetRowSpan(vertical, 3);
        grid.Children.Add(horizontal);
        grid.Children.Add(vertical);

        for (int i = 0; i < 9; i++)
        {
            int index = i;
            var dot = new Button
            {
                Width = 14,
                Height = 14,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1.5),
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

    private static bool TryRead(TextBox box, out double value)
        => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

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

        box.Text = value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    }
}
