using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Turns artwork into a definition and leaves an instance of it in the artwork's place (issue #135).
///
/// **One command, because the pieces must not be separable.** Creating a definition means four changes that are only
/// correct together: the definition joins the library, its content is a copy of the selected items, the originals
/// leave their container, and an instance that links to the definition takes their place. Half of that is a document
/// where the artwork has vanished, or one where an instance names a definition that is not there - and the library
/// had no command of its own, so an operation built from `AddDefinition` plus `RemoveItemCommand` could not be undone
/// as one step at all.
///
/// Undo puts the artwork back at the index it came from, removes the instance and drops the definition, so the
/// document is what it was.
/// </summary>
public sealed class CreateDefinitionCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly IItemContainer _container;
    private readonly LayerItem[] _items;
    private readonly int _index;
    private ArtGroup? _definition;
    private ArtGroup? _instance;

    public CreateDefinitionCommand(
        CadDocument document, IItemContainer container, IReadOnlyList<LayerItem> items, string name)
    {
        _document = document;
        _container = container;
        _items = items.ToArray();
        Name = name;

        // Where the artwork sat, so undo puts it back in the same z-place rather than on top of whatever was
        // placed after it.
        int index = -1;
        for (int i = 0; i < container.Children.Count; i++)
        {
            if (ReferenceEquals(container.Children[i], _items[0]))
            {
                index = i;
                break;
            }
        }

        _index = index >= 0 ? index : container.Children.Count;
    }

    public string Description => $"Create definition '{Name}'";

    /// <summary>The name the definition is given.</summary>
    public string Name { get; }

    /// <summary>The placement left in the artwork's place, once <see cref="Do"/> has run.</summary>
    public ArtGroup? Instance => _instance;

    /// <summary>The definition itself, once <see cref="Do"/> has run.</summary>
    public ArtGroup? Definition => _definition;

    public void Do()
    {
        if (_definition is null)
        {
            _definition = new ArtGroup { Name = Name };
            foreach (LayerItem item in _items)
            {
                _definition.AddItem((LayerItem)item.Clone());
            }
        }

        if (!_document.Definitions.Children.Contains(_definition))
        {
            _document.Definitions.AddItem(_definition);
        }

        _instance ??= (ArtGroup)_definition.Clone();
        _instance.SourceId = Name;

        foreach (LayerItem item in _items)
        {
            _container.RemoveItem(item);
        }

        _container.AddItem(_instance, _index);
    }

    public void Undo()
    {
        if (_instance is not null)
        {
            _container.RemoveItem(_instance);
        }

        for (int i = 0; i < _items.Length; i++)
        {
            _container.AddItem(_items[i], _index + i);
        }

        if (_definition is not null)
        {
            _document.Definitions.RemoveItem(_definition);
        }
    }
}

/// <summary>
/// Renames a definition and re-points every instance that named it (issue #135).
///
/// **A rename is not only a rename.** An instance names its definition by string - `ArtGroup.SourceId` holds the
/// name - so changing the definition's own name and leaving the instances alone would break every link at once: the
/// instances would keep drawing the copy they hold, `instance.refresh` would refuse the link by name, and the
/// document would look fine until someone refreshed it. Both halves are therefore one command, so undo puts the
/// link back rather than leaving instances pointing at a name that no longer exists.
/// </summary>
public sealed class RenameDefinitionCommand : IUndoableCommand
{
    private readonly ArtGroup _definition;
    private readonly string _from;
    private readonly string _to;
    private readonly (ArtGroup Instance, string Source)[] _users;

    public RenameDefinitionCommand(CadDocument document, ArtGroup definition, string to)
    {
        _definition = definition;
        _from = definition.Name;
        _to = to;

        // The users are taken now, not when `Do` runs: undo has to put back the links that were changed, and a
        // document edited between the two calls would otherwise restore a set that never existed.
        _users = document.AllGroups()
            .Where(group => InstanceResolver.IsInstance(group)
                && string.Equals(group.SourceId, _from, StringComparison.Ordinal))
            .Select(group => (group, group.SourceId))
            .ToArray();
    }

    public string Description => $"Rename definition '{_from}' to '{_to}'";

    /// <summary>How many instances were re-pointed, which is what a caller reports back.</summary>
    public int UserCount => _users.Length;

    public void Do()
    {
        _definition.Name = _to;
        foreach ((ArtGroup instance, _) in _users)
        {
            instance.SourceId = _to;
        }
    }

    public void Undo()
    {
        foreach ((ArtGroup instance, string source) in _users)
        {
            instance.SourceId = source;
        }

        _definition.Name = _from;
    }
}
