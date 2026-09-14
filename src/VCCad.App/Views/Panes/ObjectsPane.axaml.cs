using Avalonia;
using System.Collections;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avalonia.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views.Panes;

/// <summary>Object browser tab: a hierarchical tree (artboard → layer → group →
/// path) whose selection stays in sync with the canvas.</summary>
public partial class ObjectsPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;

    private Point _pressPoint;
    private TreeViewItem? _pressItem;
    private bool _dragArmed;

    public ObjectsPane()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(ObjectTree, true);
        ObjectTree.PointerPressed += OnTreePointerPressed;
        ObjectTree.PointerMoved += OnTreePointerMoved;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => RefreshTree();
        RefreshTree();
    }

    private void RefreshTree()
    {
        if (_vm is null)
        {
            return;
        }

        ObjectTree.Items.Clear();
        var pasteboard = new List<LayerItem>();

        foreach (Artboard artboard in _vm.Document.Artboards)
        {
            var board = MakeNode(artboard.Name, artboard, header: true, artboard.IsVisible,
                () => artboard.IsVisible = !artboard.IsVisible);
            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = MakeNode(FormatLayer(layer), layer, header: true, layer.IsVisible,
                    () => layer.IsVisible = !layer.IsVisible);
                foreach (LayerItem child in layer.Children)
                {
                    if (IntersectsArtboard(child, artboard))
                    {
                        AddItemNode(layerNode, child);
                    }
                    else
                    {
                        pasteboard.Add(child);
                    }
                }

                board.Items.Add(layerNode);
            }

            ObjectTree.Items.Add(board);
        }

        // Document-level orphans (objects that belong to no artboard) plus any
        // items that have drifted off their artboard are shown as pasteboard.
        pasteboard.AddRange(_vm.Document.Orphans.Children);

        if (pasteboard.Count > 0)
        {
            var paste = MakeNode("Pasteboard", null, header: true, true, () => { });
            foreach (LayerItem item in pasteboard)
            {
                AddItemNode(paste, item);
            }

            ObjectTree.Items.Add(paste);
        }

        SyncToSelection();
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
        var node = MakeNode(DescribeItem(item), item, header: false, item.IsVisible,
            () => item.IsVisible = !item.IsVisible);
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                AddItemNode(node, child);
            }
        }

        parent.Items.Add(node);
    }

    private static string DescribeItem(LayerItem item) => item switch
    {
        PathItem path => $"{path.Name} · {path.SubPaths.Sum(sp => sp.SegmentCount)} seg · {(path.IsFullyClosed ? "closed" : "open")}",
        ArtGroup group => $"▸ {group.Name}",
        _ => item.Name,
    };

    private TreeViewItem MakeNode(string text, object? tag, bool header, bool isVisible, Action toggle)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new Avalonia.Media.SolidColorBrush(
                isVisible ? Avalonia.Media.Color.FromRgb(0xE6, 0xE6, 0xE9)
                          : Avalonia.Media.Color.FromRgb(0x6A, 0x6A, 0x72)),
        };

        var eye = new Button
        {
            Content = EyeIcon(isVisible),
            Padding = new Thickness(4, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(eye, isVisible ? "Hide" : "Show");
        eye.Click += (_, e) =>
        {
            toggle();
            RefreshTree();
            e.Handled = true;
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(eye, 1);
        grid.Children.Add(label);
        grid.Children.Add(eye);

        return new TreeViewItem { Header = grid, Tag = tag, IsExpanded = header };
    }

    private static Image EyeIcon(bool visible)
    {
        var bitmap = new Bitmap(AssetLoader.Open(new Uri(
            $"avares://VCCad.App/Assets/Icons/{(visible ? "eye" : "eye-off")}.png")));
        return new Image { Source = bitmap, Width = 14, Height = 14, Stretch = Avalonia.Media.Stretch.Uniform };
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(ObjectTree);
        _pressItem = FindItem(e.Source as Visual);
        _dragArmed = e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed;
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || !e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point p = e.GetPosition(ObjectTree);
        if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6)
        {
            return;
        }

        _dragArmed = false;
        var items = new List<LayerItem>();
        foreach (object? selected in ObjectTree.SelectedItems)
        {
            if (selected is TreeViewItem { Tag: LayerItem li })
            {
                items.Add(li);
            }
        }

        if (items.Count == 0 && _pressItem?.Tag is LayerItem single)
        {
            items.Add(single);
        }

        if (items.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.Set("vccad/items", items);
        DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains("vccad/items") ? DragDropEffects.Move : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.Get("vccad/items") is not List<LayerItem> items || items.Count == 0)
        {
            return;
        }

        TreeViewItem? target = FindItem(e.Source as Visual);
        IItemContainer container;
        int index;

        if (target?.Tag is ArtGroup group)
        {
            container = group;
            index = group.Children.Count;
        }
        else if (target?.Tag is Layer layer)
        {
            container = layer;
            index = layer.Children.Count;
        }
        else if (target?.Tag is LayerItem item && item.Container is { } parent)
        {
            container = parent;
            index = IndexOf(parent, item);
            // Drop on the lower half of a row to insert after it.
            double h = target.Bounds.Height;
            if (e.GetPosition(target).Y > h / 2)
            {
                index++;
            }
        }
        else
        {
            container = _vm.Document.Orphans;
            index = container.Children.Count;
        }

        try
        {
            _vm.MoveItems(items, container, index);
        }
        catch (InvalidOperationException)
        {
            // Dropping a group into its own descendant is not allowed.
        }

        RefreshTree();
    }

    private static TreeViewItem? FindItem(Visual? source)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is TreeViewItem item)
            {
                return item;
            }
        }

        return null;
    }

    private static int IndexOf(IItemContainer container, LayerItem item)
    {
        for (int i = 0; i < container.Children.Count; i++)
        {
            if (ReferenceEquals(container.Children[i], item))
            {
                return i;
            }
        }

        return 0;
    }

    private static Rect2D BoundsOf(LayerItem item) => item switch
    {
        PathItem path => path.BoundingBox(),
        TextItem text => text.BoundingBox(),
        ArtGroup group => group.Transform.Transform(group.BoundingBox()),
        _ => Rect2D.Empty,
    };

    private static bool IntersectsArtboard(LayerItem item, Artboard artboard)
    {
        Rect2D bounds = BoundsOf(item);
        return bounds.IsEmpty || bounds.Intersects(artboard.Bounds.Inflated(0.25));
    }

    private void SyncToSelection()
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            TreeViewItem? target = null;
            if (_vm.SelectedObjects.Count > 0)
            {
                target = FindNode(ObjectTree.Items, _vm.SelectedObjects[0]);
            }

            ObjectTree.SelectedItem = target;
        }
        finally
        {
            _syncing = false;
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
        if (_syncing || _vm is null)
        {
            return;
        }

        var selected = new List<LayerItem>();
        foreach (object? entry in ObjectTree.SelectedItems)
        {
            if (entry is TreeViewItem { Tag: LayerItem li })
            {
                selected.Add(li);
            }
        }

        if (selected.Count == 1)
        {
            _vm.SelectObject(selected[0]);
        }
        else if (selected.Count > 1)
        {
            _vm.SelectRange(selected, additive: false);
        }
        else if (ObjectTree.SelectedItem is TreeViewItem { Tag: Artboard artboard })
        {
            _vm.SelectArtboard(artboard);
        }
    }
}
