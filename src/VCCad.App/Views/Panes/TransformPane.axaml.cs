using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views.Panes;

/// <summary>Transform tab: position/size/rotation fields with a 9-point pivot and
/// a live readout while dragging. Point selections show position only.</summary>
public partial class TransformPane : UserControl
{
    private EditorViewModel? _vm;
    private int _pivot = 4;
    private Button[] _pivotButtons = Array.Empty<Button>();
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E));

    public TransformPane()
    {
        InitializeComponent();
        _pivotButtons = new[] { Pivot0, Pivot1, Pivot2, Pivot3, Pivot4, Pivot5, Pivot6, Pivot7, Pivot8 };
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

    private void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        HighlightPivot();
        SetBoxText(XBox, null);
        SetBoxText(YBox, null);
        SetBoxText(WBox, null);
        SetBoxText(HBox, null);
        SetBoxText(AngleBox, null);

        SelectionInfo.Text = Describe();

        bool pointMode = _vm.HasPointSelection;
        bool objectMode = _vm.HasTransformableSelection;
        if (pointMode && _vm.PointPosition is { } pos)
        {
            SetBoxText(XBox, pos.X);
            SetBoxText(YBox, pos.Y);
            ModeNote.Text = "Point — position only (no width/height/rotation)";
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

            ModeNote.Text = "Object — position/size/rotation";
        }
        else
        {
            ModeNote.Text = "Select an object to edit its transform";
        }

        foreach (Button b in _pivotButtons)
        {
            b.IsEnabled = objectMode;
        }

        XBox.IsEnabled = pointMode || objectMode;
        YBox.IsEnabled = pointMode || objectMode;
        WBox.IsEnabled = objectMode;
        HBox.IsEnabled = objectMode;
        AngleBox.IsEnabled = objectMode;
    }

    private string Describe()
    {
        if (_vm is null || _vm.SelectedObjects.Count == 0)
        {
            return "(no selection)";
        }

        if (_vm.SelectedObjects.Count > 1)
        {
            Rect2D b = _vm.SelectionBounds();
            return $"{_vm.SelectedObjects.Count} objects\n{b.Width:0.##} × {b.Height:0.##} pt";
        }

        LayerItem item = _vm.SelectedObjects[0];
        if (item is PathItem path)
        {
            Rect2D b = path.BoundingBox();
            return $"{item.Name}\n{b.Width:0.##} × {b.Height:0.##} pt";
        }

        return item.Name;
    }

    private void HighlightPivot()
    {
        for (int i = 0; i < _pivotButtons.Length; i++)
        {
            _pivotButtons[i].Background = i == _pivot ? ActiveBrush : Brushes.Transparent;
        }
    }

    private void OnOrthoSnapChanged(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.OrthogonalSnapEnabled = OrthoSnapCheck.IsChecked == true;
        }
    }

    private void OnPivot(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            _pivot = index;
            Refresh();
        }
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
