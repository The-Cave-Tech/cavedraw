namespace VCCad.Core.Model;

/// <summary>
/// What a re-derivation did: how many paths had their effect re-translated against their current geometry, and
/// every effect that could not be re-derived.
/// </summary>
/// <param name="Refreshed">
/// The number of paths whose stroke stack was rebuilt from the live effect. A path that carries no effect is not
/// one of them - it has nothing to re-derive.
/// </param>
/// <param name="NotRefreshed">
/// One line per effect that was refused, naming the effect id, the path and the reason - a knot the edit has left
/// off the path, an effect with no segments to be parameterised along, a path of several subpaths, or an effect
/// this build does not implement. Reported rather than thrown, because the path still draws: the last conversion is
/// what it holds, and blanking it would turn an unplaceable knot into a stroke that vanished.
/// </param>
public sealed record PathEffectResolution(int Refreshed, IReadOnlyList<string> NotRefreshed);

/// <summary>
/// The step that **honours** a live path effect: it re-translates the description the path holds against the path's
/// current geometry, so the converted width profile follows the edit that changed it.
///
/// This is the half #130 left out. #130 is "convert for rendering and keep the description for export", and that is
/// still the specification - the conversion happens once, at import, and the file's own element is kept verbatim.
/// What was missing is what happens to the converted result **afterwards**. Inkscape parameterises a powerstroke's
/// knots over the whole path as <c>segmentIndex + t</c>, so the same element sits at 0.5 on a two-segment path and
/// at 0.25 on a four-segment path. A profile converted once is frozen at the segment count it was translated with,
/// and editing the path leaves it behind (issue #180).
///
/// **When this runs, and why there.** Not on read: the stored profile is read by the canvas, the exporters, the
/// stroke pane and <c>pathEffect.*</c>, and five readers deriving five answers is worse than one stored answer that
/// is occasionally stale. Not on every geometry mutation either, for the reason <see cref="InstanceResolver"/>
/// gives: edits arrive through many commands and through direct mutation, and re-deriving mid-drag would put an
/// undo entry between every mouse move. It runs as **one explicit, undoable step**
/// (<see cref="Commands.RefreshPathEffectsCommand"/>), the same discipline #117's refresh follows and the same one
/// a width profile is edited under.
///
/// **A refusal leaves the last conversion in place.** <see cref="PathEffects.Translate"/> already refuses an effect
/// it cannot place - a knot past the end of a shortened path, a path left with no segments, a path of several
/// subpaths - and those are exactly the edits that make the stored knot meaningless. The stroke keeps the profile
/// it has and the refusal is named in <see cref="PathEffectResolution.NotRefreshed"/>, because a stroke that
/// silently lost its width would look like a design decision rather than a failed derivation.
/// </summary>
public static class PathEffectResolver
{
    /// <summary>Re-derives every path's converted stroke from the live effect it holds.</summary>
    public static PathEffectResolution Resolve(CadDocument document)
    {
        int refreshed = 0;
        var notRefreshed = new List<string>();

        // The walk is over a snapshot: the loops below replace stroke stacks, and a path whose effect is inside a
        // definition is not walked at all - AllPaths covers the artboards and the pasteboard, which is where an
        // imported effect lands.
        foreach (PathItem path in document.AllPaths().ToArray())
        {
            if (path.PathEffect is not { } effect)
            {
                continue;
            }

            StrokeSpec[]? derived = Derive(effect, path, out string? refusal);
            if (derived is null)
            {
                // The path keeps what it has. Reporting the id **and** the path matters because one effect element
                // can be shared by several paths whose edits diverged, and "which knot became meaningless" is the
                // question the person has to answer.
                notRefreshed.Add($"path-effect '{effect.Id}' on '{path.Name}': {refusal}");
                continue;
            }

            path.Strokes.Clear();
            path.Strokes.AddRange(derived);
            path.NotifyStrokesChanged();
            refreshed++;
        }

        return new PathEffectResolution(refreshed, notRefreshed);
    }

    /// <summary>
    /// The stroke stack the effect describes for the path as it stands now, or null and the reason it cannot be
    /// derived. Every stroke is translated against its own cap and join, which the effect may replace - the same
    /// reading <c>pathEffect.apply</c> takes when no stroke index is given.
    /// </summary>
    private static StrokeSpec[]? Derive(PathEffectSpec effect, PathItem path, out string? refusal)
    {
        var derived = new List<StrokeSpec>(path.Strokes.Count);

        foreach (StrokeSpec stroke in path.Strokes)
        {
            PathEffectTranslation translation = PathEffects.Translate(effect, path, stroke);
            if (!translation.IsSupported)
            {
                refusal = translation.Refusal;
                return null;
            }

            derived.Add(translation.Stroke!);
        }

        refusal = null;
        return derived.ToArray();
    }
}
