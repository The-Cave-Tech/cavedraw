using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VCCad.Core.Commands;
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

    /// <summary>
    /// What the tree should be open to, kept across a rebuild.
    ///
    /// Every edit rebuilds the nodes, and a rebuilt row starts collapsed - the expansion set on the old node
    /// survives only where the old map was read first, so a selection made while a rebuild was in flight
    /// ends up inside a collapsed page again. Remembering the target and re-applying it after each rebuild
    /// makes the reveal stick rather than depending on when the rebuild happened.
    /// </summary>
    private object? _reveal;

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
            board.SetDepth(0);
            _map[artboard] = board;

            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = new ObjectNode(FormatLayer(layer), layer, layer.IsVisible, expanded.Contains(layer),
                    v => SetVisible(layer, v));
                layerNode.SetDepth(1);
                layerNode.Parent = board;
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
            paste.SetDepth(0);
            foreach (LayerItem item in pasteboard)
            {
                AddItemNode(paste, item, expanded);
            }

            _roots.Add(paste);
        }

        // A rebuilt row starts collapsed, so the row the selection is in has to be opened again -
        // otherwise an edit while something was selected hides the selection inside a collapsed page.
        if (_reveal is not null && _map.TryGetValue(_reveal, out ObjectNode? revealed))
        {
            OpenAncestors(revealed);
            revealed.IsExpanded = true;
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
    /// Delegates to <see cref="LayerTree.Classify"/>, which the object.explorer operation
    /// also uses: the panel and the API must not be able to disagree about where an object
    /// lives.
    /// </summary>
    internal static (Dictionary<Layer, List<LayerItem>> ByLayer, List<LayerItem> Pasteboard)
        Classify(CadDocument document) => LayerTree.Classify(document);

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
        => AddItemNode(parent, item, expanded, parent.Depth + 1);

    private void AddItemNode(ObjectNode parent, LayerItem item, HashSet<object> expanded, int depth)
    {
        var node = new ObjectNode(DescribeItem(item), item, item.IsVisible, expanded.Contains(item),
            v => SetVisible(item, v));
        node.SetDepth(depth);
        node.Parent = parent;
        node.Thumbnail = ObjectThumbnail.For(item, ThumbnailSize);
        _map[item] = node;

        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                AddItemNode(node, child, expanded, depth + 1);
            }
        }

        parent.Children.Add(node);
    }

    /// <summary>The box the row's sample is fitted to. Matches the template.</summary>
    private const double ThumbnailSize = 20;

    /// <summary>
    /// What the row reads. A name somebody typed, or what the object is: a panel of "Path",
    /// "Path", "Path" tells the person nothing, and the geometry already says which is a line,
    /// a rectangle or an ellipse.
    /// </summary>
    private static string DescribeItem(LayerItem item) => ObjectNaming.DisplayName(item);

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(ObjectTree);
        _pressNode = FindNode(e.Source as Visual);
        _dragArmed = e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed;

        // The menu is opened here rather than left to Avalonia's ContextRequested. That event
        // comes from the platform's own input manager, which synthetically raised pointer
        // events never pass through - so a right click was delivered, the operation reported
        // the control it hit, and no menu appeared. Opening it here means one code path for
        // every way the click can arrive.
        if (_pressNode is not null && e.GetCurrentPoint(ObjectTree).Properties.IsRightButtonPressed)
        {
            RowMenu.Open(ObjectTree);
            e.Handled = true;
        }
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

    // The traversal lives in LayerTree, which the object.explorer operation reports as well,
    // so what a driver reads and what the panel draws cannot drift apart. These forward to it.
    private static Rect2D BoundsOf(LayerItem item) => LayerTree.BoundsOf(item);

    internal static bool IntersectsArtboard(Rect2D bounds, Vector2D offset, Artboard artboard)
        => LayerTree.IntersectsArtboard(bounds, offset, artboard);

    /// <summary>The tree itself, for tests that check what the panel is showing.</summary>
    internal TreeView Tree => ObjectTree;

    /// <summary>The row showing an object, for tests that check what the tree is revealing.</summary>
    internal ObjectNode? NodeFor(object tag) => _map.TryGetValue(tag, out ObjectNode? found) ? found : null;

    /// <summary>
    /// Opens every row above a node, so the node itself is on screen.
    ///
    /// Found by walking **down** from the roots rather than up through parent links. The two must agree, and
    /// when they do not the failure is silent: an unlinked row ends the walk early, the selected item stays
    /// hidden inside a collapsed page, and the panel shows no selection at all. Searching down depends only
    /// on the tree the panel is actually drawing.
    /// </summary>
    private void OpenAncestors(ObjectNode node)
    {
        foreach (ObjectNode root in _roots)
        {
            if (OpenPath(root, node))
            {
                return;
            }
        }
    }

    private static bool OpenPath(ObjectNode current, ObjectNode target)
    {
        if (ReferenceEquals(current, target))
        {
            return true;
        }

        foreach (ObjectNode child in current.Children)
        {
            if (OpenPath(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }

        return false;
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
            // A **single** item means a fresh selection, and the tree opens to it: seeing the selection in
            // the tree is how a person learns what they just picked, which matters most when the artwork
            // sits several levels down. More than one item means they are building a selection, and
            // re-opening the panel on every additive click would move it around underneath them and lose
            // whatever they had expanded deliberately.
            if (_vm.SelectedObjects.Count != 1)
            {
                return;
            }

            // If the row is not there, the tree is showing something else - a document that has since been
            // closed, or one whose rebuild never ran. Rebuild rather than silently doing nothing: a panel
            // that is looking at the wrong document cannot reveal anything, and it fails quietly.
            if (!_map.ContainsKey(_vm.SelectedObjects[0]))
            {
                Rebuild();
            }

            if (!_map.TryGetValue(_vm.SelectedObjects[0], out ObjectNode? node))
            {
                return;
            }

            // Open the ancestors, set the selection, then open them again.
            //
            // Once is not enough, and the order alone does not fix it. Selecting a row makes the control
            // realise containers of its own, and a row that was not realised yet does not carry the
            // expansion that was set before it existed - so the page and layer above the selection could
            // end up collapsed again with the selected row hidden inside them, which is the state the
            // panel showed no selection for. Setting it on both sides of the selection costs one pass over
            // a handful of nodes and cannot be undone by the control's own bookkeeping.
            _reveal = node.Tag;
            OpenAncestors(node);
            ObjectTree.SelectedItem = node;
            OpenAncestors(node);
            ObjectTree.ScrollIntoView(node);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The row a menu action applies to: the one it was opened over, or the one that is
    /// selected.
    ///
    /// One menu serves the whole tree rather than one per row, because a row can be recycled
    /// the moment it scrolls out and a menu owned by it would end up acting on whatever the
    /// row became. That means the menu has no row of its own, so the press that opened it -
    /// or the selection, when it was opened through the API - is what says which.
    /// </summary>
    private ObjectNode? ActionRow() =>
        _pressNode
        ?? ObjectTree.SelectedItems.OfType<ObjectNode>().FirstOrDefault()
        ?? ObjectTree.SelectedItem as ObjectNode;

    /// <summary>
    /// Renames the row the menu was opened on, through the command stack so it undoes.
    ///
    /// The prompt is a small window rather than an edit box in the row: the tree is
    /// virtualised, so a row can be recycled the moment it scrolls out, and an editor living
    /// inside one would lose what had been typed into it.
    /// </summary>
    private async void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || ActionRow() is not { } node)
        {
            return;
        }

        string? name = await PromptForName(node.Name);
        if (name is null || string.Equals(name, node.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (node.Tag is LayerItem item)
        {
            _vm.Execute(new RenameItemCommand(item, name));
            _vm.NotifyDocumentChanged();
        }
    }

    private void OnShowClicked(object? sender, RoutedEventArgs e) => SetRowVisible(true);

    private void OnHideClicked(object? sender, RoutedEventArgs e) => SetRowVisible(false);

    /// <summary>
    /// Adds a layer to the active artboard - the panel's half of `layer.add`.
    ///
    /// The registry has had that operation while this panel had no control calling it, so the assistant could
    /// add a layer and a person working in the Layers tab could not (#236). Both now run the same
    /// artboard.AddLayer and the same document-changed notification.
    /// </summary>
    private void OnAddLayerClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Document.Artboards.Count == 0)
        {
            return;
        }

        _vm.Document.Artboards[0].AddLayer((string?)null);
        _vm.NotifyDocumentChanged();
    }
    private void SetRowVisible(bool visible)
    {
        if (ActionRow() is { } node)
        {
            node.IsVisible = visible;
        }
    }

    /// <summary>Asks for a name. Returns null when the person cancelled.</summary>
    private async Task<string?> PromptForName(string current)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var box = new TextBox { Text = current, Width = 280, Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "Rename", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? result = null;

        var dialog = new Window
        {
            Title = "Rename",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 6) },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();
        box.AttachedToVisualTree += (_, _) => box.SelectAll();

        await dialog.ShowDialog(owner);
        return result;
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
