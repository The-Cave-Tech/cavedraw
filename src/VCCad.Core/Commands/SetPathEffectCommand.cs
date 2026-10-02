using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Installs a live path effect's own description on a path, as the state it can be re-derived from.
///
/// Issue #180's two residues are both about how a description becomes state. <c>RefreshPathEffectsCommand</c>
/// gives the description a step that honours it, but the only thing that ever wrote one was the SVG reader - so an
/// effect converted through the operation registry, or described by hand, was not live and the refresh step had
/// nothing to re-derive. This is the write half: it holds the <see cref="PathEffectSpec"/> the reader builds (and
/// <c>pathEffect.get</c> reports) on the path, where <see cref="PathEffectResolver"/> will find it.
///
/// It deliberately does **not** convert. Setting the description and deriving the drawing are two steps, and
/// <see cref="RefreshPathEffectsCommand"/> is the second, so a caller (and an undo stack) sees the description
/// being installed and the conversion it produces as the separate edits they are.
///
/// It captures the description it replaces at the moment it applies, like every other command in this stack, so
/// redo after an intervening edit is correct rather than restoring a value captured when the command was built.
/// </summary>
public sealed class SetPathEffectCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly PathEffectSpec _effect;

    private PathEffectSpec? _previous;

    /// <summary>Creates the installation of <paramref name="effect"/> on <paramref name="path"/>.</summary>
    public SetPathEffectCommand(PathItem path, PathEffectSpec effect, string description = "Set live path effect")
    {
        _path = path;
        _effect = effect;
        Description = description;
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public void Do()
    {
        _previous = _path.PathEffect;
        _path.PathEffect = _effect;
    }

    /// <inheritdoc/>
    public void Undo() => _path.PathEffect = _previous;
}
