using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Sets or clears the marker a path names for one of SVG's three marker properties (issue #202).
///
/// A marker reference is part of the path - it is what makes the arrowhead follow the path when the path is edited -
/// so changing one is an edit like any other and has to be undoable. The command holds the **previous** value,
/// including "null" for a slot that named nothing, so undo restores exactly what was there rather than clearing a
/// reference that used to exist.
/// </summary>
public sealed class SetMarkerCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly MarkerProperty _slot;
    private readonly string? _before;
    private readonly string? _after;

    public SetMarkerCommand(PathItem path, MarkerProperty slot, string? name)
    {
        _path = path;
        _slot = slot;
        _before = Read(path, slot);
        _after = string.IsNullOrEmpty(name) ? null : name;
    }

    public string Description => _after is null
        ? $"Clear the {_slot.ToString().ToLowerInvariant()} marker"
        : $"Set the {_slot.ToString().ToLowerInvariant()} marker to '{_after}'";

    public void Do() => Write(_path, _slot, _after);

    public void Undo() => Write(_path, _slot, _before);

    /// <summary>Which of SVG's three marker properties a reference belongs to.</summary>
    public enum MarkerProperty
    {
        Start,
        Mid,
        End,
    }

    private static string? Read(PathItem path, MarkerProperty slot) => slot switch
    {
        MarkerProperty.Start => path.MarkerStart,
        MarkerProperty.Mid => path.MarkerMid,
        _ => path.MarkerEnd,
    };

    private static void Write(PathItem path, MarkerProperty slot, string? name)
    {
        switch (slot)
        {
            case MarkerProperty.Start:
                path.MarkerStart = name;
                break;
            case MarkerProperty.Mid:
                path.MarkerMid = name;
                break;
            default:
                path.MarkerEnd = name;
                break;
        }
    }
}
