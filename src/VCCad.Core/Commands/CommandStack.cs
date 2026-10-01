namespace VCCad.Core.Commands;

/// <summary>
/// The document-level undo/redo stack.
///
/// The stack holds a bounded history of executed commands plus a "redo tail".
/// <see cref="Execute"/> runs a command immediately, discards any stale redo tail
/// (standard editor semantics: new edits invalidate undone history), then pushes
/// the command. <see cref="Undo"/>/<see cref="Redo"/> walk the current position.
///
/// Automation sessions each own a CommandStack, so two scripted clients cannot
/// corrupt one another's history. A single shared stack is used when the UI and
/// an automation client intentionally operate on the same live document.
/// </summary>
public sealed class CommandStack
{
    private readonly List<IUndoableCommand> _history = new();
    private int _position; // number of commands currently applied (0.._history.Count)

    /// <summary>Maximum number of commands retained in history.</summary>
    public int Limit { get; }

    /// <summary>Fired after every Execute/Undo/Redo/Clear that changes the position.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates a stack retaining at most <paramref name="limit"/> commands.</summary>
    public CommandStack(int limit = 1000)
    {
        Limit = Math.Max(1, limit);
    }

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _position > 0;

    /// <summary>
    /// How many edits are currently applied - the depth an undo walks back through.
    ///
    /// Read-only, and it stays that way: the position is the stack's to move. It exists because "was that gesture
    /// one edit or three" has to be answerable from outside the stack, and counting by undoing answers it by
    /// changing the answer.
    /// </summary>
    public int Depth => _position;

    /// <summary>True when an undo has left a redo-able tail.</summary>
    public bool CanRedo => _position < _history.Count;

    /// <summary>Human description of the command that an Undo would reverse, or null.</summary>
    public string? UndoDescription => _position > 0 ? _history[_position - 1].Description : null;

    /// <summary>Human description of the command that Redo would re-apply, or null.</summary>
    public string? RedoDescription => _position < _history.Count ? _history[_position].Description : null;

    /// <summary>
    /// Applies <paramref name="command"/> and records it. Redo history is dropped.
    /// When the limit is exceeded the oldest commands are evicted, which also
    /// reduces the undo position so eviction never leaves stale entries.
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        command.Do();

        // Truncate any redone tail first so the history is linear.
        if (_position < _history.Count)
        {
            _history.RemoveRange(_position, _history.Count - _position);
        }

        _history.Add(command);
        _position++;

        // Evict the oldest commands beyond the retention limit.
        int excess = _history.Count - Limit;
        if (excess > 0)
        {
            _history.RemoveRange(0, excess);
            _position -= excess;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reverses the most recently applied command, if any.</summary>
    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        _history[_position - 1].Undo();
        _position--;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Re-applies the next undone command, if any.</summary>
    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        _history[_position].Do();
        _position++;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Clears history without touching the document (used after a load).</summary>
    public void Clear()
    {
        _history.Clear();
        _position = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
