namespace VCCad.Core.Commands;

/// <summary>
/// Gives an object a name somebody chose.
///
/// Two things have to come back on undo: the name it had, and whether that name was the
/// person's or the panel's guess from its geometry. Restoring the string alone would turn a
/// derived name into a fixed one, so undoing a rename would leave the object stuck reading
/// "Rectangle" for ever after - a small wrong that outlives the undo.
/// </summary>
public sealed class RenameItemCommand : IUndoableCommand
{
    private readonly Model.CadObject _item;
    private readonly string _name;

    private string _previousName = string.Empty;
    private bool _previousWasUserSet;

    /// <summary>Creates a rename of <paramref name="item"/> to <paramref name="name"/>.</summary>
    public RenameItemCommand(Model.CadObject item, string name)
    {
        _item = item;
        _name = name;
    }

    /// <inheritdoc/>
    public string Description => $"Rename to \u201c{_name}\u201d";

    /// <inheritdoc/>
    public void Do()
    {
        // Captured here rather than in the constructor: the contract says a command records
        // the state it replaces at the moment it applies, which is what makes redo correct
        // after an intervening edit.
        _previousName = _item.Name;
        _previousWasUserSet = _item.NameIsUserSet;

        _item.Name = _name;
        _item.NameIsUserSet = true;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _item.Name = _previousName;
        _item.NameIsUserSet = _previousWasUserSet;
    }
}
