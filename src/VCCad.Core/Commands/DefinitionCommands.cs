using VCCad.Core.Model;

namespace VCCad.Core.Commands;

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
