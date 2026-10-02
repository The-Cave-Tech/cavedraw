using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Tree-housekeeping shared by the two container types (<see cref="Layer"/> and
/// <see cref="ArtGroup"/>). Kept internal so the add/remove invariants live in one
/// place and cannot be bypassed by future container implementations.
///
/// Invariants enforced here:
/// <list type="bullet">
/// <item>An item is removed from any previous container before being re-homed.</item>
/// <item>An item can never be inserted beneath one of its own descendants
/// (cycle prevention — only relevant for groups).</item>
/// <item>The <see cref="CadObject.Container"/> back-pointer is always kept in sync.</item>
/// </list>
/// </summary>
internal static class ItemTree
{
    /// <summary>
    /// Inserts <paramref name="item"/> into <paramref name="list"/> on behalf of
    /// <paramref name="owner"/>, at <paramref name="zIndex"/> (top when null).
    /// </summary>
    public static int Insert(LayerItem item, IItemContainer owner, List<LayerItem> list, int? zIndex)
    {
        if (item == owner)
        {
            throw new ArgumentException("An item cannot contain itself.", nameof(item));
        }

        // A group must never be moved inside one of its own descendants.
        if (owner is ArtGroup group && IsDescendantOf(item, group))
        {
            throw new InvalidOperationException("Cannot nest a group inside its own descendant.");
        }

        // Re-home: pull the item out of whatever container currently holds it.
        if (item.Container is not null && item.Container != owner)
        {
            item.Container.RemoveItem(item);
        }
        else if (item.Container == owner)
        {
            list.Remove(item);
        }

        int index = zIndex.HasValue
            ? Math.Clamp(zIndex.Value, 0, list.Count)
            : list.Count;

        list.Insert(index, item);
        item.Container = owner;

        // Ownership follows the structure: an item moved to another document, or onto (or off) the
        // pasteboard, has to know which document its coordinates are stored relative to before anything
        // asks where it is drawn. A reparent that skipped this would leave the item claiming the frame it
        // no longer lives in, which is the same class of mistake as storing the wrong numbers (#174).
        Own(item, (owner as CadObject)?.Document);

        return index;
    }

    /// <summary>
    /// Records which document an object and everything under it belongs to, so an item's frame can be
    /// asked for without walking to an artboard it may no longer be under.
    /// </summary>
    public static void Own(CadObject node, CadDocument? document)
    {
        node.Document = document;

        switch (node)
        {
            case IItemContainer container:
                foreach (LayerItem child in container.Children)
                {
                    Own(child, document);
                }

                break;
        }
    }

    /// <summary>Removes the item from the list and clears its back-pointer.</summary>
    public static bool Remove(LayerItem item, IItemContainer owner, List<LayerItem> list)
    {
        int index = list.IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        list.RemoveAt(index);
        if (item.Container == owner)
        {
            item.Container = null;
        }

        return true;
    }

    /// <summary>True when <paramref name="candidate"/> sits somewhere above
    /// <paramref name="group"/> in the tree (i.e. is an ancestor of the group).</summary>
    private static bool IsDescendantOf(LayerItem candidate, ArtGroup group)
    {
        for (IItemContainer? walk = group.Container; walk is not null; walk = (walk as LayerItem)?.Container)
        {
            if (walk == candidate)
            {
                return true;
            }
        }

        return false;
    }
}
