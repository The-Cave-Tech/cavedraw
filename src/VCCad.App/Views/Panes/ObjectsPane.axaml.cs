using System.Collections;
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

    public ObjectsPane()
    {
        InitializeComponent();
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
            var board = MakeNode(artboard.Name, artboard, header: true);
            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = MakeNode(FormatLayer(layer), layer, header: true);
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

        if (pasteboard.Count > 0)
        {
            var paste = MakeNode("Pasteboard (off-artboard)", null, header: true);
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

    private static string DescribeItem(LayerItem item) => item switch
    {
        PathItem path => $"{path.Name} · {path.SubPaths.Sum(sp => sp.SegmentCount)} seg · {(path.IsFullyClosed ? "closed" : "open")}",
        ArtGroup group => $"▸ {group.Name}",
        _ => item.Name,
    };

    private static TreeViewItem MakeNode(string text, object? tag, bool header = false)
        => new() { Header = text, Tag = tag, IsExpanded = header };

    private static Rect2D BoundsOf(LayerItem item) => item switch
    {
        PathItem path => path.BoundingBox(),
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

        if (ObjectTree.SelectedItem is TreeViewItem { Tag: LayerItem item })
        {
            _vm.SelectObject(item);
        }
        else if (ObjectTree.SelectedItem is TreeViewItem { Tag: Artboard artboard })
        {
            _vm.SelectArtboard(artboard);
        }
    }
}
