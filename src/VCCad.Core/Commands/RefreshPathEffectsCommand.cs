using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Re-derives every live path effect in the document against the geometry it is on, as one undo step.
///
/// **This is the edit path that keeps an imported Inkscape effect alive.** An effect is converted once, at import,
/// into a width profile - which is #130's specification and stays that way - but a powerstroke's knots are stored
/// as a segment index over the whole path, so the converted profile is only correct for the path it was translated
/// against. Move a node, add one, or delete one and the profile describes where the knots used to be. This command
/// is the step that re-reads the effect the path carries and rebuilds the profile from the current geometry, so an
/// edit is not left behind (issue #180).
///
/// It captures each path's stroke stack before the re-derivation and puts it back on undo, because undo has to
/// restore the previous **converted result** rather than derive again: after the geometry was edited back, deriving
/// again would produce the same profile the command produced, not the one the document held before it ran.
///
/// It is a command rather than a hook on geometry mutation for the reasons <see cref="PathEffectResolver"/> gives:
/// edits arrive through many paths, a re-derivation per mouse move would put an undo entry between every one, and a
/// knot an edit has made unplaceable has to be refused by name rather than recursed into. The refused cases are
/// reported by <see cref="PathEffectResolution.NotRefreshed"/> and the path keeps the profile it had, so an effect
/// that cannot be recomputed is visible instead of silently drawing no width.
/// </summary>
public sealed class RefreshPathEffectsCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly List<Snapshot> _before = new();

    public RefreshPathEffectsCommand(CadDocument document, string description = "Refresh live path effects")
    {
        _document = document;
        Description = description;
    }

    public string Description { get; }

    /// <summary>What the last <see cref="Do"/> re-derived and what it could not.</summary>
    public PathEffectResolution? LastResolution { get; private set; }

    public void Do()
    {
        _before.Clear();

        // Only the paths that carry an effect: those are the only ones the resolution can touch, and a snapshot of
        // every path in the document would be an undo entry's worth of state that nothing could change.
        foreach (PathItem path in _document.AllPaths().Where(path => path.PathEffect is not null).ToArray())
        {
            _before.Add(new Snapshot(path, path.Strokes.ToArray()));
        }

        LastResolution = PathEffectResolver.Resolve(_document);
    }

    public void Undo()
    {
        for (int i = _before.Count - 1; i >= 0; i--)
        {
            Snapshot snapshot = _before[i];
            snapshot.Path.Strokes.Clear();
            snapshot.Path.Strokes.AddRange(snapshot.Strokes);
            snapshot.Path.NotifyStrokesChanged();
        }

        LastResolution = null;
    }

    private readonly record struct Snapshot(PathItem Path, StrokeSpec[] Strokes);
}
