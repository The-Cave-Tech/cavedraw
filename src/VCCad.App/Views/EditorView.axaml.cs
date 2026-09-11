using System.Collections;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views;

/// <summary>
/// Code-behind for the editor chrome: shuttles between XAML and the
/// <see cref="EditorViewModel"/>, builds the hierarchical object tree, keeps the
/// tree selection in sync with the canvas selection (and vice versa), and shows
/// which tool is active.
/// </summary>
public partial class EditorView : UserControl
{
    private readonly EditorViewModel _viewModel = new();
    private bool _syncingTreeSelection;

    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0xDD, 0xF7));

    public EditorView()
    {
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.DocumentChanged += OnDocumentChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.TransformChanged += (_, _) => RefreshTransformFields();

        Workspace.AttachEditor(_viewModel);

        _pivotButtons = new[] { Pivot0, Pivot1, Pivot2, Pivot3, Pivot4, Pivot5, Pivot6, Pivot7, Pivot8 };
        foreach (TextBox box in _fieldBoxes)
        {
            box.LostFocus += (_, _) => CommitFromField(box);
        }

        UpdateStatusAndZoom();
        RefreshTree();
        HighlightActiveTool();
        HighlightPivot();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        UpdateStatusAndZoom();
        RefreshTree();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Status))
        {
            UpdateStatusAndZoom();
        }
        else if (e.PropertyName is nameof(EditorViewModel.Tool)
                 or nameof(EditorViewModel.PrimarySelection)
                 or nameof(EditorViewModel.SelectedObjects))
        {
            HighlightActiveTool();
            UpdateStatusAndZoom();
        }
    }

    private void UpdateStatusAndZoom()
    {
        StatusText.Text = _viewModel.Status;
        ZoomLabel.Text = $"{Workspace.Zoom * 100:0.##}%";
        SelectionInfo.Text = DescribeSelection();
        RefreshTransformFields();
        RefreshStyleFields();
    }

    // ------------------------------------------------------------------
    // Color & Stroke tabs
    // ------------------------------------------------------------------

    private void RefreshStyleFields()
    {
        if (_viewModel.PrimarySelection is not PathItem path)
        {
            foreach (TextBox box in new[] { FillR, FillG, FillB, StrokeR, StrokeG, StrokeB, StrokeWidthBox, MiterBox })
            {
                SetBoxText(box, null);
            }

            return;
        }

        SetBoxText(FillR, Math.Round(path.Fill.Color.R * 255));
        SetBoxText(FillG, Math.Round(path.Fill.Color.G * 255));
        SetBoxText(FillB, Math.Round(path.Fill.Color.B * 255));
        FillRuleBox.SelectedIndex = path.Fill.Rule == ModelFillRule.EvenOdd ? 1 : 0;

        SetBoxText(StrokeR, Math.Round(path.Stroke.Color.R * 255));
        SetBoxText(StrokeG, Math.Round(path.Stroke.Color.G * 255));
        SetBoxText(StrokeB, Math.Round(path.Stroke.Color.B * 255));

        SetBoxText(StrokeWidthBox, path.Stroke.Width);
        SetBoxText(MiterBox, path.Stroke.MiterLimit);
        StrokeCapBox.SelectedIndex = path.Stroke.Cap switch
        {
            StrokeCap.Round => 1,
            StrokeCap.Square => 2,
            _ => 0,
        };
        StrokeJoinBox.SelectedIndex = path.Stroke.Join switch
        {
            StrokeJoin.Round => 1,
            StrokeJoin.Bevel => 2,
            _ => 0,
        };
    }

    private static ColorRgb ReadColor(TextBox r, TextBox g, TextBox b)
    {
        byte Channel(TextBox box)
        {
            return double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? (byte)Math.Clamp(Math.Round(v), 0, 255)
                : (byte)0;
        }

        return ColorRgb.FromBytes(Channel(r), Channel(g), Channel(b));
    }

    private void OnApplyFill(object? sender, RoutedEventArgs e)
    {
        ColorRgb color = ReadColor(FillR, FillG, FillB);
        ModelFillRule rule = FillRuleBox.SelectedIndex == 1 ? ModelFillRule.EvenOdd : ModelFillRule.NonZero;
        _viewModel.ApplyFill(color, rule);
        UpdateStatusAndZoom();
    }

    private void OnApplyStrokeColor(object? sender, RoutedEventArgs e)
    {
        _viewModel.ApplyStrokeColor(ReadColor(StrokeR, StrokeG, StrokeB));
        UpdateStatusAndZoom();
    }

    private void OnApplyStroke(object? sender, RoutedEventArgs e)
    {
        double width = TryRead(StrokeWidthBox, out double w) ? w : 1.0;
        double miter = TryRead(MiterBox, out double m) ? m : 4.0;
        StrokeCap cap = StrokeCapBox.SelectedIndex switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };
        StrokeJoin join = StrokeJoinBox.SelectedIndex switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
        _viewModel.ApplyStroke(width, cap, join, miter);
        UpdateStatusAndZoom();
    }

    // ------------------------------------------------------------------
    // Numeric Transform panel (X / Y / W / H / rotation + 9-point pivot)
    // ------------------------------------------------------------------

    private Button[] _pivotButtons = Array.Empty<Button>();
    private int _pivot = 4; // centre

    private TextBox[] _fieldBoxes => new[] { XBox, YBox, WBox, HBox, AngleBox };

    private void HighlightPivot()
    {
        for (int i = 0; i < _pivotButtons.Length; i++)
        {
            _pivotButtons[i].Background = i == _pivot ? ActiveBrush : Brushes.Transparent;
        }
    }

    private void OnOrthoSnapChanged(object? sender, RoutedEventArgs e)
    {
        _viewModel.OrthogonalSnapEnabled = OrthoSnapCheck.IsChecked == true;
    }

    private void OnPivot(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            _pivot = index;
            HighlightPivot();
            RefreshTransformFields();
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

    /// <summary>Commits whatever numeric change the just-edited field represents.</summary>
    private void CommitFromField(TextBox field)
    {
        if (_viewModel.HasPointSelection)
        {
            CommitPointField(field);
            return;
        }

        if (_viewModel.HasTransformableSelection)
        {
            CommitObjectFields();
        }
    }

    private void CommitPointField(TextBox field)
    {
        if (_viewModel.PointPosition is not { } current)
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

        _viewModel.MovePointTo(target);
        RefreshTransformFields();
    }

    private void CommitObjectFields()
    {
        (Rect2D bounds, double currentAngle) = _viewModel.TransformReadout();
        if (bounds.IsEmpty)
        {
            return;
        }

        Point2D pivot = ReferencePoint(bounds, _pivot);
        Point2D currentRef = ReferencePoint(bounds, _pivot);

        Vector2D translation = new();
        double? targetX = TryRead(XBox, out double rx) ? rx : null;
        double? targetY = TryRead(YBox, out double ry) ? ry : null;
        if (targetX.HasValue || targetY.HasValue)
        {
            Point2D desired = new(targetX ?? currentRef.X, targetY ?? currentRef.Y);
            translation = desired - currentRef;
        }

        double scaleX = 1.0, scaleY = 1.0;
        if (TryRead(WBox, out double w) && bounds.Width > 1e-6)
        {
            scaleX = w / bounds.Width;
        }

        if (TryRead(HBox, out double h) && bounds.Height > 1e-6)
        {
            scaleY = h / bounds.Height;
        }

        double rotationDelta = 0.0;
        if (TryRead(AngleBox, out double angle))
        {
            rotationDelta = angle - currentAngle;
        }

        _viewModel.ApplyTransform(pivot, translation, scaleX, scaleY, rotationDelta);
        RefreshTransformFields();
    }

    private static bool TryRead(TextBox box, out double value)
    {
        return double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Reference point for a 9-cell matrix: row-major 0..8.</summary>
    private static Point2D ReferencePoint(Rect2D bounds, int pivot)
    {
        double x = (pivot % 3) switch { 0 => bounds.Left, 1 => bounds.Center.X, _ => bounds.Right };
        double y = (pivot / 3) switch { 0 => bounds.Top, 1 => bounds.Center.Y, _ => bounds.Bottom };
        return new Point2D(x, y);
    }

    private void RefreshTransformFields()
    {
        HighlightPivot();
        SetBoxText(XBox, null);
        SetBoxText(YBox, null);
        SetBoxText(WBox, null);
        SetBoxText(HBox, null);
        SetBoxText(AngleBox, null);

        bool pointMode = _viewModel.HasPointSelection;
        bool objectMode = _viewModel.HasTransformableSelection;
        bool any = pointMode || objectMode;

        if (pointMode && _viewModel.PointPosition is { } pos)
        {
            SetBoxText(XBox, pos.X);
            SetBoxText(YBox, pos.Y);
            TransformModeNote.Text = "Point — position only (no width/height/rotation)";
        }
        else if (objectMode)
        {
            (Rect2D bounds, double angle) = _viewModel.TransformReadout();
            if (!bounds.IsEmpty)
            {
                Point2D reference = ReferencePoint(bounds, _pivot);
                SetBoxText(XBox, reference.X);
                SetBoxText(YBox, reference.Y);
                SetBoxText(WBox, bounds.Width);
                SetBoxText(HBox, bounds.Height);
                SetBoxText(AngleBox, Math.Round(angle, 3));
            }

            TransformModeNote.Text = _viewModel.SelectedObjects.Count > 1
                ? "Selection bounds — position/size/rotation"
                : "Object — position/size/rotation";
        }
        else
        {
            TransformModeNote.Text = "Select an object to edit its transform";
        }

        foreach (Button button in _pivotButtons)
        {
            button.IsEnabled = objectMode;
        }

        XBox.IsEnabled = any;
        YBox.IsEnabled = any;
        WBox.IsEnabled = objectMode;
        HBox.IsEnabled = objectMode;
        AngleBox.IsEnabled = objectMode;
    }

    /// <summary>Sets a field's text unless the user is editing that field.</summary>
    private void SetBoxText(TextBox box, double? value)
    {
        if (box.IsFocused)
        {
            return;
        }

        box.Text = value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private string DescribeSelection()
    {
        IReadOnlyList<LayerItem> objects = _viewModel.SelectedObjects;
        if (objects.Count == 0)
        {
            return "(no selection)";
        }

        if (objects.Count > 1)
        {
            Rect2D b = _viewModel.SelectionBounds();
            return $"{objects.Count} objects selected\nBounds: {b.X:0.##}, {b.Y:0.##} → {b.Right:0.##}, {b.Bottom:0.##}pt";
        }

        LayerItem item = objects[0];
        int segmentCount = _viewModel.SelectedSegments().Count(s => s.Path == item);

        if (item is PathItem path)
        {
            int segments = path.SubPaths.Sum(sp => sp.SegmentCount);
            Rect2D b = path.BoundingBox();
            string fill = path.Fill.IsVisible ? "fill" : "no-fill";
            string stroke = path.Stroke.HasVisibleOutline
                ? $"stroke {path.Stroke.Width:0.##}pt"
                : "no-stroke";
            string extra = segmentCount > 0 ? $"\n{segmentCount} segment(s) selected — drag the orange handles to reshape" : string.Empty;
            return $"Path '{item.Name}'\n{segments} segment(s), {fill}, {stroke}\n" +
                   $"Bounds: {b.X:0.##}, {b.Y:0.##} → {b.Right:0.##}, {b.Bottom:0.##}pt{extra}";
        }

        if (item is ArtGroup group)
        {
            return $"Group '{item.Name}'\n{CountItems(group)} item(s)";
        }

        return item.Name;
    }

    private static int CountItems(ArtGroup group)
    {
        int count = 0;
        foreach (LayerItem child in group.Children)
        {
            count += child is ArtGroup nested ? 1 + CountItems(nested) : 1;
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Object tree
    // ------------------------------------------------------------------

    private void RefreshTree()
    {
        if (ObjectTree.Items is not null)
        {
            ObjectTree.Items.Clear();
        }

        // Objects whose whole bounds lie outside any artboard (i.e. living on the
        // pasteboard) are shown at the top level of the tree — not nested under an
        // artboard — mirroring how artwork "off the page" reads in Illustrator.
        var pasteboardItems = new List<LayerItem>();

        foreach (Artboard artboard in _viewModel.Document.Artboards)
        {
            var boardNode = MakeNode(artboard.Name, artboard, isHeader: true);
            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = MakeNode(FormatLayer(layer), layer, isHeader: true);
                foreach (LayerItem child in layer.Children)
                {
                    if (IntersectsArtboard(child, artboard))
                    {
                        AddItemNode(layerNode, child);
                    }
                    else
                    {
                        pasteboardItems.Add(child);
                    }
                }

                boardNode.Items.Add(layerNode);
            }

            ObjectTree.Items.Add(boardNode);
        }

        if (pasteboardItems.Count > 0)
        {
            var pasteNode = MakeNode("Pasteboard (off-artboard)", null, isHeader: true);
            foreach (LayerItem item in pasteboardItems)
            {
                AddItemNode(pasteNode, item);
            }

            ObjectTree.Items.Add(pasteNode);
        }

        SyncTreeToSelection();
    }

    /// <summary>World-space bounds of an item (identity-transform hierarchies).</summary>
    private static Rect2D BoundsOf(LayerItem item)
    {
        switch (item)
        {
            case PathItem path:
                return path.BoundingBox();
            case ArtGroup group:
                return group.Transform.Transform(group.BoundingBox());
            default:
                return Rect2D.Empty;
        }
    }

    /// <summary>
    /// True when the item's <em>whole</em> bounds overlap the artboard. Individual
    /// points/segments poking off the page do not count — only whole objects.
    /// </summary>
    private static bool IntersectsArtboard(LayerItem item, Artboard artboard)
    {
        Rect2D bounds = BoundsOf(item);
        if (bounds.IsEmpty)
        {
            return true; // nothing measurable — keep it where its layer is
        }

        // Overlap test (inclusive edges) against a slightly enlarged artboard so
        // a shape sitting flush on the edge is still treated as on the page.
        Rect2D page = artboard.Bounds.Inflated(0.25);
        return bounds.Intersects(page);
    }

    private static string FormatLayer(Layer layer)
    {
        string flags = layer.IsVisible ? string.Empty : " · hidden";
        if (layer.IsLocked)
        {
            flags += " · locked";
        }

        return $"{layer.Name}{flags}";
    }

    private void AddItemNode(TreeViewItem parent, LayerItem item)
    {
        var node = MakeNode(DescribeItem(item), item);
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                AddItemNode(node, child);
            }
        }

        parent.Items.Add(node);
    }

    private static string DescribeItem(LayerItem item)
    {
        return item switch
        {
            PathItem path => $"{NameOf(path.Name, "path")} · {DescribePathBrief(path)}",
            ArtGroup group => $"▸ {NameOf(group.Name, "Group")}",
            _ => item.Name,
        };
    }

    private static string DescribePathBrief(PathItem path)
    {
        string kind = path.IsFullyClosed ? "closed" : "open";
        string segments = path.SubPaths.Sum(sp => sp.SegmentCount).ToString();
        return $"{segments} seg · {kind}";
    }

    private static string NameOf(string name, string fallback)
        => string.IsNullOrWhiteSpace(name) ? fallback : name;

    private static TreeViewItem MakeNode(string text, object? tag, bool isHeader = false)
    {
        var node = new TreeViewItem { Header = text, Tag = tag, IsExpanded = isHeader };
        return node;
    }

    /// <summary>Highlights the first selected object in the tree, or clears it.</summary>
    private void SyncTreeToSelection()
    {
        _syncingTreeSelection = true;
        try
        {
            TreeViewItem? toSelect = null;
            if (_viewModel.SelectedObjects.Count > 0)
            {
                toSelect = FindNode(ObjectTree.Items, _viewModel.SelectedObjects[0]);
            }

            ObjectTree.SelectedItem = toSelect;
        }
        finally
        {
            _syncingTreeSelection = false;
        }
    }

    private static TreeViewItem? FindNode(IEnumerable items, LayerItem target)
    {
        foreach (object child in items)
        {
            if (child is not TreeViewItem node)
            {
                continue;
            }

            if (ReferenceEquals(node.Tag, target))
            {
                return node;
            }

            if (FindNode(node.Items, target) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTreeSelection)
        {
            return;
        }

        if (ObjectTree.SelectedItem is TreeViewItem { Tag: LayerItem item })
        {
            _viewModel.SelectObject(item);
        }
    }

    // ------------------------------------------------------------------
    // Tool highlighting
    // ------------------------------------------------------------------

    private void HighlightActiveTool()
    {
        SetActive(ToolSelectButton, _viewModel.Tool == EditorTool.Select);
        SetActive(ToolNodeButton, _viewModel.Tool == EditorTool.Node);
        SetActive(ToolPenButton, _viewModel.Tool == EditorTool.Pen);
        SetActive(ToolRectButton, _viewModel.Tool == EditorTool.Rectangle);
        SetActive(ToolEllipseButton, _viewModel.Tool == EditorTool.Ellipse);
    }

    private static void SetActive(Button button, bool active)
        => button.Background = active ? ActiveBrush : Brushes.Transparent;

    private void SetTool(EditorTool tool)
    {
        _viewModel.Tool = tool;
        _viewModel.Status = tool switch
        {
            EditorTool.Select => "Selection tool (V): click to pick, Shift+click for multiple, drag to move all",
            EditorTool.Node => "Node tool (A): drag anchors/handles, click a segment to select it (Shift adds)",
            EditorTool.Pen => "Pen (P): click adds anchors, drag pulls a handle, click the start anchor to close",
            EditorTool.Rectangle => "Rectangle: drag between two corners",
            EditorTool.Ellipse => "Ellipse: drag between two corners of its bounding box",
            _ => _viewModel.Status,
        };
        HighlightActiveTool();
        UpdateStatusAndZoom();
    }

    // ------------------------------------------------------------------
    // File / Edit / Tools / View handlers
    // ------------------------------------------------------------------

    private void OnNew(object? sender, RoutedEventArgs e) => _viewModel.NewDocument();
    private void OnOpen(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.LoadFromServerAsync(), "Opening…");
    private void OnSave(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.SaveToServerAsync(), "Saving…");

    private async void FireAndForget(Task task, string busyText)
    {
        StatusText.Text = busyText;
        try
        {
            await task;
        }
        finally
        {
            UpdateStatusAndZoom();
        }
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => _viewModel.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _viewModel.Redo();
    private void OnDelete(object? sender, RoutedEventArgs e) => _viewModel.DeleteSelection();
    private void OnToolSelect(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Select);
    private void OnToolNode(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Node);
    private void OnToolPen(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Pen);
    private void OnToolRectangle(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Rectangle);
    private void OnToolEllipse(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Ellipse);

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Workspace.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Workspace.ZoomOut();
    private void OnActualSize(object? sender, RoutedEventArgs e) => Workspace.ZoomToActualSize();
    private void OnFitInWindow(object? sender, RoutedEventArgs e) => Workspace.ZoomToFit();

    private void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        byte[] pdf = _viewModel.ExportPdf();
        SelectionInfo.Text = $"PDF payload ready: {pdf.Length:N0} bytes";
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Exit is handled by the browser tab itself.";
    }
}
