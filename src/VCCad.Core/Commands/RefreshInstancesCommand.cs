using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Rebuilds every instance in the document from the definition it names, as one undo step.
///
/// **This is the edit path that keeps a link alive.** A definition is an asset: a person changes it - moves a node,
/// swaps a fill, adds a shape - and every instance of it has to show the change, or the link is a fact the model
/// records and never honours. An instance holds a copy of the definition's content as well as the id it came from,
/// so the change reaches it only by re-resolving.
///
/// The command captures each instance's children before the resolution and puts them back on undo, because undo has
/// to restore the previous *content* rather than resolve again: a second resolution would restore the state the
/// document was edited away from, not the state before this command ran.
///
/// It is a command rather than a hook on the definition's own mutation for the reasons
/// <see cref="InstanceResolver"/> gives: edits arrive through many paths, a mid-drag rebuild would multiply undo
/// entries, and a cycle introduced by an edit has to be refused by name rather than recursed into. Deleting a
/// definition is the case that needs saying out loud: the instances are **left with the copy they hold** and the
/// dangling id is reported by <see cref="InstanceResolution.NotFollowed"/> and by
/// <see cref="CadDocument.MissingDefinitions"/>, so a lost asset is visible instead of looking like a design
/// decision.
/// </summary>
public sealed class RefreshInstancesCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly List<Snapshot> _before = new();

    public RefreshInstancesCommand(CadDocument document, string description = "Refresh instances")
    {
        _document = document;
        Description = description;
    }

    public string Description { get; }

    /// <summary>What the last <see cref="Do"/> resolved and what it could not follow.</summary>
    public InstanceResolution? LastResolution { get; private set; }

    public void Do()
    {
        _before.Clear();

        // Every instance, outermost or not: each one's children are captured, so no instance is left holding the
        // copy it had if the resolution refuses its link. The order is tree order, and undo restores in reverse so
        // a nested instance is put back before the instance that contains it.
        foreach (ArtGroup instance in _document.AllGroups()
            .Where(group => InstanceResolver.IsInstance(group))
            .ToArray())
        {
            _before.Add(new Snapshot(instance, instance.Children.ToArray()));
        }

        LastResolution = InstanceResolver.Resolve(_document);
    }

    public void Undo()
    {
        for (int i = _before.Count - 1; i >= 0; i--)
        {
            Snapshot snapshot = _before[i];

            foreach (LayerItem child in snapshot.Instance.Children.ToArray())
            {
                snapshot.Instance.RemoveItem(child);
            }

            foreach (LayerItem child in snapshot.Children)
            {
                snapshot.Instance.AddItem(child);
            }
        }

        LastResolution = null;
    }

    private readonly record struct Snapshot(ArtGroup Instance, LayerItem[] Children);
}
