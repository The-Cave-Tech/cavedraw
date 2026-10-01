using VCCad.Core.Commands;
using VCCad.Core.Model;

namespace VCCad.App.Commands;

/// <summary>
/// Edits the document's filter library, and the items that draw through a filter, as **one** undo step.
///
/// A filter is referred to **by name**, so changing one is never a change to the library alone. Deleting a filter
/// has to clear the items that named it, and applying one has to remember what the selection was drawing through
/// beforehand - otherwise Undo puts the library back and leaves the artwork pointing at a filter nobody restores,
/// which is the "quietly drawn unfiltered" state <see cref="CadDocument.MissingFilters"/> exists to report.
///
/// This is deliberately the same shape as <see cref="EditWidthProfilesCommand"/>, for the same reason: a named
/// asset with referrers is edited as a unit, or undo is not the inverse of the edit.
///
/// The item half is captured as (item, before, after) rather than by rewriting the tree, because an item's
/// reference is one string and the item itself is stable for the duration of the edit.
/// </summary>
public sealed class FilterEditCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly List<FilterSpec> _libraryBefore;
    private readonly List<FilterSpec> _libraryAfter;
    private readonly List<ItemEdit> _items;

    public FilterEditCommand(
        CadDocument document,
        IEnumerable<FilterSpec> libraryAfter,
        IEnumerable<ItemEdit> items,
        string description)
    {
        _document = document;
        _libraryBefore = document.Filters.ToList();
        _libraryAfter = libraryAfter.ToList();
        _items = items.ToList();
        Description = description;
    }

    /// <summary>One item's filter reference, before and after, at a fixed place in the tree.</summary>
    public sealed record ItemEdit(LayerItem Item, string? Before, string? After);

    public string Description { get; }

    public void Do() => Apply(_libraryAfter, after: true);

    public void Undo() => Apply(_libraryBefore, after: false);

    /// <summary>
    /// Puts one state of the library and its referrers back.
    ///
    /// The library is rewritten rather than patched a name at a time because its **order** is part of the
    /// document: the serializer writes the filters in this order, so an undo that restored the same filters in a
    /// different order would give a file that is not identical to the one before the edit.
    /// </summary>
    private void Apply(IReadOnlyList<FilterSpec> library, bool after)
    {
        foreach (FilterSpec existing in _document.Filters.ToList())
        {
            _document.RemoveFilter(existing.Name);
        }

        foreach (FilterSpec filter in library)
        {
            _document.AddFilter(filter);
        }

        foreach (ItemEdit edit in _items)
        {
            edit.Item.FilterId = after ? edit.After : edit.Before;
        }
    }
}
