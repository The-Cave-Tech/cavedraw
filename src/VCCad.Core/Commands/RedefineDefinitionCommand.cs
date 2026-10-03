using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Replaces a definition's content with artwork from the canvas (issues #135 and #202).
///
/// **A definition has no canvas to be edited on.** It lives in the document's library, which is not on an artboard,
/// so a person cannot select its content and draw into it - and "the arrowhead geometry is editable" stayed
/// theoretical for exactly that reason. The route that needs no new mode is the one Illustrator uses: edit a copy
/// where you can see it, then take the result into the definition. This is that second half.
///
/// The instances do **not** move: an instance holds its own placement, and its content follows on
/// `instance.refresh`, which is what "an instance is a reference, not a copy" means. The selection itself is left
/// alone, because the artwork a person drew is theirs - redefining must not silently consume it.
///
/// Undo restores the definition's previous content, which is held by reference rather than cloned: nothing mutates
/// it in between, and copying a definition's whole content to be able to undo an edit that may never be undone is
/// the expensive half of the wrong trade.
/// </summary>
public sealed class RedefineDefinitionCommand : IUndoableCommand
{
    private readonly ArtGroup _definition;
    private readonly LayerItem[] _items;
    private readonly LayerItem[] _before;

    public RedefineDefinitionCommand(ArtGroup definition, IReadOnlyList<LayerItem> items)
    {
        _definition = definition;
        _items = items.ToArray();
        _before = definition.Children.ToArray();
    }

    public string Description => $"Redefine '{_definition.Name}'";

    /// <summary>How much artwork the definition now holds, which is what a caller reports back.</summary>
    public int ChildCount => _items.Length;

    public void Do() => Replace(_items.Select(item => (LayerItem)item.Clone()).ToArray());

    public void Undo() => Replace(_before);

    /// <summary>
    /// Swaps the definition's content. `Children` is read-only, so the old content is removed first - from a copy of
    /// the list, because removing while enumerating it is the other way to lose an item silently.
    /// </summary>
    private void Replace(IReadOnlyList<LayerItem> items)
    {
        foreach (LayerItem child in _definition.Children.ToArray())
        {
            _definition.RemoveItem(child);
        }

        foreach (LayerItem item in items)
        {
            _definition.AddItem(item);
        }
    }
}
