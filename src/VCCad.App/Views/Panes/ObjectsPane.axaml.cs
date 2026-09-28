using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views.Panes;

/// <summary>Object browser tab: a data-bound, virtualised tree (artboard → layer →
/// group → path) whose selection stays in sync with the canvas. Nodes are plain
/// view-models, so a document with thousands of objects does not create thousands
/// of controls up front.</summary>
public partial class ObjectsPane : UserControl
{
    private static readonly object PasteboardTag = new();
    private readonly ObservableCollection<ObjectNode> _roots = new();
    private readonly Dictionary<object, ObjectNode> _map = new(ReferenceEqualityComparer.Instance);

    private EditorViewModel? _vm;
    private bool _syncing;

    private Point _pressPoint;
    private ObjectNode? _pressNode;
    private bool _dragArmed;

    public ObjectsPane()
    {
        InitializeComponent();
        ObjectTree.ItemsSource = _roots;
        DragDrop.SetAllowDrop(ObjectTree, true);
        ObjectTree.PointerPressed += OnTreePointerPressed;
        ObjectTree.PointerMoved += OnTreePointerMoved;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Rebuild();
        vm.SelectionChanged += (_, _) => SyncToSelection();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_vm is null)
        {
            return;
        }

        // Remember which nodes the user expanded so a rebuild (which happens on
        // every edit) does not collapse the tree again.
        var expanded = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (ObjectNode node in _map.Values)
        {
            if (node.IsExpanded && node.Tag is not null)
            {
                expanded.Add(node.Tag);
            }
        }

        _roots.Clear();
        _map.Clear();

        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> loose) = Classify(_vm.Document);
        var pasteboard = new List<LayerItem>();

        foreach (Artboard artboard in _vm.Document.Artboards)
        {
            var board = new ObjectNode(artboard.Name, artboard, artboard.IsVisible, expanded.Contains(artboard),
                v => SetVisible(artboard, v));
            _map[artboard] = board;

            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = new ObjectNode(FormatLayer(layer), layer, layer.IsVisible, expanded.Contains(layer),
                    v => SetVisible(layer, v));
                _map[layer] = layerNode;

                HashSet<LayerItem> mine = byLayer.TryGetValue(layer, out List<LayerItem>? items)
                    ? new HashSet<LayerItem>(items, ReferenceEqualityComparer.Instance)
                    : new HashSet<LayerItem>(ReferenceEqualityComparer.Instance);

                foreach (LayerItem child in layer.Children)
                {
                    if (mine.Contains(child))
                    {
                        AddItemNode(layerNode, child, expanded);
                    }
                    else
                    {
                        pasteboard.Add(child);
                    }
                }

                board.Children.Add(layerNode);
            }

            _roots.Add(board);
        }

        pasteboard.AddRange(loose);

        if (pasteboard.Count > 0)
        {
            var paste = new ObjectNode("Pasteboard", PasteboardTag, true, expanded.Contains(PasteboardTag), _ => { });
            foreach (LayerItem item in pasteboard)
            {
                AddItemNode(paste, item, expanded);
            }

            _roots.Add(paste);
        }

        SyncToSelection();
    }

    private void SetVisible(LayerItem item, bool visible)
    {
        item.IsVisible = visible;
        _vm?.RaiseTransformChanged();
    }

    private void SetVisible(Artboard artboard, bool visible)
    {
        artboard.IsVisible = visible;
        _vm?.RaiseTransformChanged();
    }

    private void SetVisible(Layer layer, bool visible)
    {
        layer.IsVisible = visible;
        _vm?.RaiseTransformChanged();
    }

    /// <summary>
    /// Which items belong to which layer, and which belong to no artboard at all.
    ///
    /// Split out from <see cref="Rebuild"/> so the placement rule can be exercised without
    /// an Avalonia visual tree — a tiled pattern laid out as a grid is the case that broke,
    /// and it needs twelve artboards and a few hundred items to show up.
    /// </summary>
    internal static (Dictionary<Layer, List<LayerItem>> ByLayer, List<LayerItem> Pasteboard)
        Classify(CadDocument document)
    {
        var byLayer = new Dictionary<Layer, List<LayerItem>>();
        var pasteboard = new List<LayerItem>();

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                var mine = new List<LayerItem>();
                byLayer[layer] = mine;

                foreach (LayerItem child in layer.Children)
                {
                    if (IntersectsArtboard(BoundsOf(child), child.ArtboardOffset(), artboard))
                    {
                        mine.Add(child);
                    }
                    else
                    {
                        pasteboard.Add(child);
                    }
                }
            }
        }

        pasteboard.AddRange(document.Orphans.Children);
        return (byLayer, pasteboard);
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

    private void AddItemNode(ObjectNode parent, LayerItem item, HashSet<object> expanded)
    {
        var node = new ObjectNode(DescribeItem(item), item, item.IsVisible, expanded.Contains(item),
            v => SetVisible(item, v));
        _map[item] = node;

        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                AddItemNode(node, child, expanded);
            }
        }

        parent.Children.Add(node);
    }

    private static string DescribeItem(LayerItem item) => item switch
    {
        PathItem path => $"{path.Name} · {path.SubPaths.Sum(sp => sp.SegmentCount)} seg · {(path.IsFullyClosed ? "closed" : "open")}",
        ArtGroup group => $"▸ {group.Name}",
        _ => item.Name,
    };

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(ObjectTree);
        _pressNode = FindNode(e.Source as Visual);
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
        foreach (ObjectNode node in ObjectTree.SelectedItems.OfType<ObjectNode>())
        {
            if (node.Tag is LayerItem li)
            {
                items.Add(li);
            }
        }

        if (items.Count == 0 && _pressNode?.Tag is LayerItem single)
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

        TreeViewItem? container = FindContainer(e.Source as Visual);
        ObjectNode? target = container?.DataContext as ObjectNode;
        IItemContainer targetContainer;
        int index;

        if (target?.Tag is ArtGroup group)
        {
            targetContainer = group;
            index = group.Children.Count;
        }
        else if (target?.Tag is Layer layer)
        {
            targetContainer = layer;
            index = layer.Children.Count;
        }
        else if (target?.Tag is LayerItem item && item.Container is { } parent)
        {
            targetContainer = parent;
            index = IndexOf(parent, item);
            // Drop on the lower half of a row to insert after it.
            double h = container!.Bounds.Height;
            if (e.GetPosition(container).Y > h / 2)
            {
                index++;
            }
        }
        else
        {
            targetContainer = _vm.Document.Orphans;
            index = targetContainer.Children.Count;
        }

        try
        {
            _vm.MoveItems(items, targetContainer, index);
        }
        catch (InvalidOperationException)
        {
            // Dropping a group into its own descendant is not allowed.
        }

        Rebuild();
    }

    private static TreeViewItem? FindContainer(Visual? source)
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

    private static ObjectNode? FindNode(Visual? source)
        => FindContainer(source)?.DataContext as ObjectNode;

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

    /// <summary>
    /// Whether an item belongs to an artboard, in document space.
    ///
    /// Item coordinates are stored relative to their artboard, so an item's own bounds are
    /// near the origin while an artboard's are its place in the sheet's grid. Comparing the
    /// two directly asked "is this item near the top-left of the whole document", which is
    /// only ever true of the first page: every item on every other page failed the test and
    /// was filed under the Pasteboard. A tiled pattern - twelve sheets laid out in a grid -
    /// showed one page's contents and a collapsed Pasteboard holding the rest.
    ///
    /// Takes the bounds rather than the item so the placement rule can be exercised without
    /// an Avalonia visual tree.
    /// </summary>
    internal static bool IntersectsArtboard(Rect2D bounds, Vector2D offset, Artboard artboard)
    {
        // Nothing to place: a group with no geometry, or bounds not yet computed. Showing it
        // under its artboard is better than hiding it on the pasteboard.
        if (bounds.IsEmpty)
        {
            return true;
        }

        var page = new Rect2D(
            bounds.Left + offset.X, bounds.Top + offset.Y, bounds.Width, bounds.Height);

        return page.Intersects(artboard.Bounds.Inflated(0.25));
    }

    private void SyncToSelection()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (_vm.SelectedObjects.Count > 0 && _map.TryGetValue(_vm.SelectedObjects[0], out ObjectNode? node))
            {
                ObjectTree.SelectedItem = node;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        var selected = new List<LayerItem>();
        Artboard? artboard = null;
        foreach (ObjectNode node in ObjectTree.SelectedItems.OfType<ObjectNode>())
        {
            switch (node.Tag)
            {
                case LayerItem item:
                    selected.Add(item);
                    break;
                case Artboard board:
                    artboard = board;
                    break;
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
        else if (artboard is not null)
        {
            _vm.SelectArtboard(artboard);
        }
    }
}
