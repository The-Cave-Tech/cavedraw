using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Runs a batch of sub-commands as a single undo step (e.g. moving ten selected
/// paths in one drag = one Undo). Do executes each sub-command in order; Undo
/// reverses them in reverse order, exactly like a nested command stack.
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;

    public string Description { get; }

    public CompositeCommand(string description, IEnumerable<IUndoableCommand> commands)
    {
        Description = description;
        _commands = commands.Where(c => c is not null).ToArray();
    }

    public void Do()
    {
        foreach (IUndoableCommand command in _commands)
        {
            command.Do();
        }
    }

    public void Undo()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }
}
