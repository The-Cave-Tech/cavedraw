using VCCad.App.ViewModels;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Automation;

/// <summary>
/// The object clipboard: what Ctrl+C, Ctrl+X and Ctrl+V act on when a person has **objects** selected
/// rather than text being edited.
///
/// It exists because the two halves of this project's parity rule met here. A person presses Ctrl+C
/// and expects the copy; a driver must be able to do the same thing, which is `object.copy`,
/// `object.cut` and `object.paste` - and because both routes call this class, they cannot drift
/// apart. Before it, `Ctrl+C` did nothing at all unless a text caret was open, and the clipboard
/// below the text editor was a private field on the canvas that nothing outside could reach.
///
/// Copies are **clones**, taken when the copy is made. Cutting or deleting the original afterwards
/// must not change what was copied - that is the whole point of a clipboard - and a paste that held
/// references would paste whatever the original had become.
/// </summary>
internal static class ObjectClipboard
{
    private static readonly List<LayerItem> _items = new();

    /// <summary>How many objects are held, so a caller can report an empty paste rather than a silent one.</summary>
    public static int Count => _items.Count;

    /// <summary>
    /// Takes a clone of every selected object, replacing whatever was held.
    ///
    /// Returns how many were taken, which is zero when nothing is selected: a copy of nothing is a
    /// clipboard that has not changed, not one that has been emptied.
    /// </summary>
    public static int Copy(DocumentSession session)
    {
        LayerItem[] selected = session.SelectedObjects.ToArray();
        if (selected.Length == 0)
        {
            return 0;
        }

        _items.Clear();
        foreach (LayerItem item in selected)
        {
            _items.Add((LayerItem)item.Clone());
        }

        return _items.Count;
    }

    /// <summary>
    /// Copies the selection and then removes it, as Ctrl+X does. The removal goes through the
    /// session's own delete so it is undoable and marks the document modified like any other edit.
    /// </summary>
    public static int Cut(DocumentSession session)
    {
        int copied = Copy(session);
        if (copied == 0)
        {
            return 0;
        }

        session.DeleteSelection();
        return copied;
    }

    /// <summary>
    /// Pastes what is held and selects the copies, so the very next gesture moves them.
    ///
    /// **Pasted in place, deliberately.** The copy lands exactly on the original and is selected, so
    /// one arrow key takes it clear. The alternative - offsetting it here - would make a paste two
    /// undo steps, the placement and the nudge, and Undo would have to be pressed twice to take back
    /// one paste. A clipboard whose undo is exact is worth more than one that guesses an offset.
    ///
    /// Where it lands follows the same rule as drawing a new shape: <see cref="DocumentSession.TargetFor"/>
    /// decides, so a paste next to a shape on a page goes on that page's layer rather than onto
    /// whichever layer happened to be active.
    /// </summary>
    public static LayerItem[] Paste(DocumentSession session)
    {
        if (_items.Count == 0)
        {
            return Array.Empty<LayerItem>();
        }

        var clones = new List<LayerItem>(_items.Count);
        var edits = new List<IUndoableCommand>(_items.Count);

        foreach (LayerItem held in _items)
        {
            var clone = (LayerItem)held.Clone();
            (Layer layer, _) = session.TargetFor(CentreOf(clone));
            edits.Add(new AddItemCommand(layer, clone));
            clones.Add(clone);
        }

        session.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Paste", edits));
        session.SelectRange(clones, additive: false);
        return clones.ToArray();
    }

    /// <summary>
    /// Where a pasted copy would land, so the layer it goes on follows the same rule a newly drawn
    /// shape follows. The switch is the one the object summaries already use: every item kind answers
    /// the same question differently, and a group answers it for everything inside it.
    /// </summary>
    private static Point2D CentreOf(LayerItem item)
    {
        Rect2D bounds = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            ArtGroup group => group.BoundingBox(),
            ImageItem image => image.WorldBounds(),
            _ => Rect2D.Empty,
        };

        return bounds.IsEmpty ? new Point2D(0, 0) : bounds.Center;
    }
}
