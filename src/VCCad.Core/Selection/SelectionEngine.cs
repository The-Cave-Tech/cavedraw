using VCCad.Geometry;
using VCCad.Core.Model;

namespace VCCad.Core.Selection;

/// <summary>
/// What the pointer did, in document coordinates.
///
/// The selection rules are worked out from a list of these rather than from live pointer
/// events, so a test can replay the same gestures a person made without a window, a canvas or
/// a hit-testable control anywhere in sight.
/// </summary>
public abstract record SelectEvent;

/// <summary>The pointer went down.</summary>
/// <param name="Point">Where, in document coordinates.</param>
/// <param name="Extend">Whether the additive modifier was held.</param>
public sealed record PointerDown(Point2D Point, bool Extend = false) : SelectEvent;

/// <summary>The pointer moved with the button down.</summary>
public sealed record PointerMove(Point2D Point) : SelectEvent;

/// <summary>The pointer came up.</summary>
public sealed record PointerUp(Point2D Point, bool Extend = false) : SelectEvent;

/// <summary>What a gesture selected.</summary>
/// <param name="Items">The objects selected, outermost first.</param>
/// <param name="Artboards">The artboards selected whole, if any.</param>
/// <param name="Focused">The artboard the gesture focused, if any.</param>
public sealed record SelectionResult(
    IReadOnlyList<LayerItem> Items,
    IReadOnlyList<Artboard> Artboards,
    Artboard? Focused)
{
    /// <summary>Nothing selected and nothing focused.</summary>
    public static SelectionResult Empty { get; } =
        new(Array.Empty<LayerItem>(), Array.Empty<Artboard>(), null);
}

/// <summary>
/// Decides what a click or a marquee selects.
///
/// It is here rather than in the canvas because the rules are about the document - which
/// artboard a point is in, what a clip leaves of an object, what a marquee encloses - and none
/// of that needs a window. The canvas asks it what a gesture meant and then draws the answer.
///
/// The first rule is the one that was wrong: a point belongs to the artboard that CONTAINS it,
/// and to no other. Testing it against every artboard's local coordinates in turn made a click
/// on page 1 select whatever happened to sit under the same offset on page 6.
/// </summary>
public static class SelectionEngine
{
    /// <summary>How near a path a click counts as being on it, in document units.</summary>
    public const double PickTolerance = 3.0;

    /// <summary>
    /// The artboard a point is in, or null when it is outside all of them.
    ///
    /// The first that contains it, so overlapping artboards resolve in document order rather
    /// than by whichever happened to be tested last.
    /// </summary>
    public static Artboard? ArtboardAt(IEnumerable<Artboard> artboards, Point2D point)
    {
        foreach (Artboard artboard in artboards)
        {
            if (artboard.IsVisible && artboard.Bounds.Contains(point))
            {
                return artboard;
            }
        }

        return null;
    }

    /// <summary>
    /// What a click at a point selects.
    ///
    /// Inside an artboard, the point focuses that artboard and picks the nearest path within
    /// it. Outside every artboard, nothing is picked and - unless something is already
    /// selected - the focus is cleared.
    /// </summary>
    public static SelectionResult Click(
        CadDocument document, Point2D point, Artboard? focused = null)
    {
        Artboard? board = ArtboardAt(document.Artboards, point);

        if (board is null)
        {
            // A click on the pasteboard picks nothing, and lets go of the artboard that was
            // focused. Objects that live off the page are still selectable by their own
            // bounds, but only when something is actually under the pointer.
            LayerItem? loose = OrphanAt(document, point);
            return loose is null
                ? SelectionResult.Empty
                : new SelectionResult(new[] { loose }, Array.Empty<Artboard>(), null);
        }

        LayerItem? hit = Within(board, point);
        return hit is null
            ? new SelectionResult(Array.Empty<LayerItem>(), Array.Empty<Artboard>(), board)
            : new SelectionResult(new[] { hit }, Array.Empty<Artboard>(), board);
    }

    /// <summary>The topmost object of an artboard under a point, or null.</summary>
    public static LayerItem? Within(Artboard artboard, Point2D point)
    {
        Point2D local = point - new Vector2D(artboard.X, artboard.Y);
        LayerItem? topmost = null;

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsEffectivelyVisible)
            {
                continue;
            }

            foreach (LayerItem item in layer.Children)
            {
                if (Hits(item, local, PickTolerance))
                {
                    topmost = item;
                }
            }
        }

        return topmost;
    }

    /// <summary>The topmost pasteboard object under a point, or null. World coordinates.</summary>
    public static LayerItem? OrphanAt(CadDocument document, Point2D point)
    {
        LayerItem? topmost = null;

        foreach (LayerItem item in document.Orphans.Children)
        {
            if (Hits(item, point, PickTolerance))
            {
                topmost = item;
            }
        }

        return topmost;
    }

    /// <summary>
    /// Whether a point lands on an object.
    ///
    /// An object clipped by its parent is only there where the clip leaves it: the parts a
    /// parent has cut away are not selectable, however solid the object's own geometry looks.
    /// </summary>
    public static bool Hits(LayerItem item, Point2D point, double tolerance)
    {
        if (!item.IsEffectivelyVisible() || item.IsLocked)
        {
            return false;
        }

        if (!Survives(item, point))
        {
            return false;
        }

        return item switch
        {
            PathItem path => PathHits(path, point, tolerance),
            TextItem text => text.BoundingBox().Inflated(tolerance).Contains(point),
            ImageItem image => image.Placement.Inflated(tolerance).Contains(point),
            ArtGroup group => group.Children.Any(c => Hits(c, point, tolerance)),
            _ => false,
        };
    }

    /// <summary>Whether a point is inside every clip the object carries.</summary>
    private static bool Survives(LayerItem item, Point2D point)
    {
        foreach (ClipSpec clip in item.Clips)
        {
            if (!clip.Contains(point))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathHits(PathItem path, Point2D point, double tolerance)
    {
        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.BoundingBox().Inflated(tolerance).Contains(point))
            {
                return true;
            }
        }

        return false;
    }
}
