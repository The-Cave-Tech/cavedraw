namespace VCCad.Core.Commands;

/// <summary>
/// A single reversible operation on the document graph (Command pattern).
///
/// Contract:
/// <list type="bullet">
/// <item><see cref="Do"/> applies the change. It must leave the model in a valid
/// state and must be repeatable after an <see cref="Undo"/>.</item>
/// <item><see cref="Undo"/> reverses <see cref="Do"/> exactly. It is called in
/// strict LIFO order by the <see cref="CommandStack"/>, which lets commands
/// capture transient state (insertion index, prior value) during <c>Do</c> when
/// that state only exists at execution time.</item>
/// <item>Commands are small and capture only what the inverse needs — never
/// whole-document snapshots, which would defeat undo limits and memory budgets.</item>
/// </list>
///
/// Because commands are the automation surface (ADR-04: the UI and the JSON-RPC
/// API both execute the same command types), implementations must be deterministic
/// and free of UI concerns.
/// </summary>
public interface IUndoableCommand
{
    /// <summary>Short human phrase for undo/redo menus, e.g. "Add layer".</summary>
    string Description { get; }

    /// <summary>Applies the change.</summary>
    void Do();

    /// <summary>Reverses the change, restoring the pre-<see cref="Do"/> state.</summary>
    void Undo();
}
