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

    /// <summary>
    /// The region of an object that is actually there, after every clip on it.
    ///
    /// This is what makes clipping a selection concern: a rectangle cut down to a sliver is
    /// only that sliver, so a marquee that encloses the sliver encloses the object and one
    /// that encloses only the cut-away part does not. Non-rectangular clips count - the
    /// intersection is taken against the clip's own outline, not its box.
    /// </summary>
    public static Polygon VisibleRegion(LayerItem item, Vector2D offset)
    {
        Polygon region = Flatten(item, offset);

        if (region.IsEmpty)
        {
            return region;
        }

        foreach (ClipSpec clip in ClipsOn(item))
        {
            var rings = new List<IEnumerable<Point2D>>();

            foreach (SubPath sub in clip.SubPaths)
            {
                rings.Add(Flatten(sub, offset).Points);
            }

            // An even-odd clip has holes, so its rings go in together: clipping by each ring
            // separately would keep the contents of the hole.
            region = region.ClipToConvex(new Polygon(rings));

            if (region.IsEmpty)
            {
                return region;
            }
        }

        return region;
    }

    /// <summary>An object's own outline, flattened and moved into document coordinates.</summary>
    private static Polygon Flatten(LayerItem item, Vector2D offset)
    {
        var rings = new List<IEnumerable<Point2D>>();

        switch (item)
        {
            case PathItem path:
                foreach (SubPath sub in path.SubPaths)
                {
                    rings.Add(Flatten(sub, offset).Points);
                }

                break;

            case TextItem text:
                rings.Add(Box(text.BoundingBox(), offset));
                break;

            case ImageItem image:
                rings.Add(Box(image.Placement, offset));
                break;

            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    Polygon childRegion = VisibleRegion(child, offset);
                    if (!childRegion.IsEmpty)
                    {
                        rings.Add(childRegion.Points);
                    }
                }

                break;
        }

        return new Polygon(rings);
    }

    /// <summary>The four corners of a rectangle, moved by an offset.</summary>
    private static IEnumerable<Point2D> Box(Rect2D box, Vector2D offset) => new[]
    {
        new Point2D(box.Left + offset.X, box.Top + offset.Y),
        new Point2D(box.Right + offset.X, box.Top + offset.Y),
        new Point2D(box.Right + offset.X, box.Bottom + offset.Y),
        new Point2D(box.Left + offset.X, box.Bottom + offset.Y),
    };

    /// <summary>
    /// A subpath as a polygon, with curves walked at a fixed number of steps.
    ///
    /// Straight segments come out exact. A curve does not, and does not need to: the answers
    /// wanted are "is this point in it" and "is it inside that", and a fine approximation
    /// gives those for any shape a person would draw.
    /// </summary>
    private static Polygon Flatten(SubPath sub, Vector2D offset)
    {
        var points = new List<Point2D>();
        const int Steps = 12;

        if (sub.Nodes.Count == 0)
        {
            return new Polygon(points);
        }

        Point2D At(Point2D p) => new(p.X + offset.X, p.Y + offset.Y);
        points.Add(At(sub.Nodes[0].Anchor));

        int segments = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;

        for (int i = 0; i < segments; i++)
        {
            (int start, int end) = sub.SegmentEndNodes(i);
            PathNode from = sub.Nodes[start];
            PathNode to = sub.Nodes[end];

            if (Near(from.OutHandle, from.Anchor) && Near(to.InHandle, to.Anchor))
            {
                points.Add(At(to.Anchor));
                continue;
            }

            CubicBezier curve = sub.GetSegment(i);
            for (int step = 1; step <= Steps; step++)
            {
                points.Add(At(curve.PointAt((double)step / Steps)));
            }
        }

        return new Polygon(points);
    }

    private static bool Near(Point2D a, Point2D b)
        => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    /// <summary>
    /// A marquee's path: a rectangle as a rectangular PATH, so the same code serves a lasso.
    ///
    /// The corners are the two the pointer went between, in the order visited, and a marquee
    /// can be dragged in any direction - so they are normalised here rather than by every
    /// caller, which is how a drag up and to the left ends up selecting nothing.
    /// </summary>
    public static Polygon MarqueePath(Point2D first, Point2D second)
    {
        double left = Math.Min(first.X, second.X);
        double right = Math.Max(first.X, second.X);
        double top = Math.Min(first.Y, second.Y);
        double bottom = Math.Max(first.Y, second.Y);

        return new Polygon(new[]
        {
            new Point2D(left, top), new Point2D(right, top),
            new Point2D(right, bottom), new Point2D(left, bottom),
        });
    }

    /// <summary>
    /// What a marquee selects, dragged from <paramref name="from"/> to <paramref name="to"/>.
    ///
    /// The rules depend on where the drag STARTED, not where it currently is:
    ///
    /// - Started inside an artboard, it selects that artboard's objects and disqualifies
    ///   everything else. Growing past the artboard's edge does not change the rule - a drag
    ///   that began on a page is about that page however far it is taken, and losing the rule
    ///   mid-gesture would change what the person is doing without them doing anything.
    /// - Started outside every artboard, it selects what it touches on the first artboard it
    ///   meets; once it encloses an artboard whole, the artboard is selected instead of its
    ///   contents, and every other artboard it reaches comes whole as well.
    /// </summary>
    /// <param name="focused">The artboard already focused, if any.</param>
    public static SelectionResult Marquee(
        CadDocument document, Point2D from, Point2D to, Artboard? focused = null)
    {
        Polygon path = MarqueePath(from, to);
        Artboard? startedIn = ArtboardAt(document.Artboards, from) ?? focused;

        return startedIn is not null
            ? WithinArtboard(startedIn, path)
            : AcrossArtboards(document, path);
    }

    /// <summary>
    /// A marquee that began on a page: that page's objects, and nothing else.
    ///
    /// Growing beyond the page is allowed and changes nothing. "Objects outside of the focused
    /// artboard are disqualified from selection" is the rule, and it stays the rule however
    /// large the marquee gets.
    /// </summary>
    private static SelectionResult WithinArtboard(Artboard artboard, Polygon path)
    {
        var selected = new List<LayerItem>();
        Vector2D offset = new(artboard.X, artboard.Y);

        foreach (LayerItem item in ObjectsOf(artboard))
        {
            if (Enclosed(item, offset, path))
            {
                selected.Add(item);
            }
        }

        // The artboard stays focused for the whole drag, even once the pointer has left it.
        return new SelectionResult(selected, Array.Empty<Artboard>(), artboard);
    }

    /// <summary>
    /// A marquee that began on the pasteboard: artboards it covers whole, or the contents of
    /// the first one it touches.
    /// </summary>
    private static SelectionResult AcrossArtboards(CadDocument document, Polygon path)
    {
        var whole = new List<Artboard>();
        Artboard? firstTouched = null;
        var firstContents = new List<LayerItem>();

        foreach (Artboard artboard in document.Artboards)
        {
            if (!artboard.IsVisible)
            {
                continue;
            }

            Polygon box = ArtboardOutline(artboard);

            if (box.IsInside(path))
            {
                // Enclosing a page whole selects the page, not what is printed on it.
                whole.Add(artboard);
                continue;
            }

            if (!box.Intersects(path))
            {
                continue;
            }

            // Touched but not enclosed. While nothing is enclosed whole, this is where the
            // contents come from - the first page met, and no later one.
            if (whole.Count == 0 && firstTouched is null)
            {
                firstTouched = artboard;
                Vector2D offset = new(artboard.X, artboard.Y);

                foreach (LayerItem item in ObjectsOf(artboard))
                {
                    if (Enclosed(item, offset, path))
                    {
                        firstContents.Add(item);
                    }
                }
            }
        }

        if (whole.Count == 0)
        {
            return new SelectionResult(firstContents, Array.Empty<Artboard>(), firstTouched);
        }

        // Something is enclosed whole, so pages win outright: every other artboard the marquee
        // reaches comes with it, and none of their contents do.
        foreach (Artboard artboard in document.Artboards)
        {
            if (artboard.IsVisible && !whole.Contains(artboard) &&
                ArtboardOutline(artboard).Intersects(path))
            {
                whole.Add(artboard);
            }
        }

        return new SelectionResult(Array.Empty<LayerItem>(), whole, whole[0]);
    }

    /// <summary>An artboard's page box as a polygon.</summary>
    private static Polygon ArtboardOutline(Artboard artboard) => new(new[]
    {
        new Point2D(artboard.Bounds.Left, artboard.Bounds.Top),
        new Point2D(artboard.Bounds.Right, artboard.Bounds.Top),
        new Point2D(artboard.Bounds.Right, artboard.Bounds.Bottom),
        new Point2D(artboard.Bounds.Left, artboard.Bounds.Bottom),
    });

    /// <summary>Every object directly under an artboard's layers.</summary>
    private static IEnumerable<LayerItem> ObjectsOf(Artboard artboard)
        => artboard.Layers.Where(l => l.IsEffectivelyVisible).SelectMany(l => l.Children);

    /// <summary>
    /// Whether a marquee encloses what is actually visible of an object.
    ///
    /// The object's region after its clips is what is tested, so an object cut away to nothing
    /// by a parent is not selected by a marquee over where it used to be, and one cut down to
    /// a corner is selected only by a marquee that reaches that corner.
    /// </summary>
    private static bool Enclosed(LayerItem item, Vector2D offset, Polygon path)
    {
        if (!item.IsEffectivelyVisible() || item.IsLocked)
        {
            return false;
        }

        Polygon region = VisibleRegion(item, offset);
        return !region.IsEmpty && region.IsInside(path);
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

    /// <summary>
    /// Every clip that applies to an object: its own, and those of the groups above it.
    ///
    /// A clipping mask clips what is inside it, not merely itself, so a shape nested two
    /// groups deep is bounded by both. Without walking up, a child looked unclipped however
    /// tightly its parents held it - which is the other half of the report.
    /// </summary>
    public static IReadOnlyList<ClipSpec> ClipsOn(LayerItem item)
    {
        var clips = new List<ClipSpec>();

        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is LayerItem ancestor && ancestor.Clips.Count > 0)
            {
                clips.AddRange(ancestor.Clips);
            }
        }

        clips.AddRange(item.Clips);
        return clips;
    }

    /// <summary>Whether a point survives every clip that applies to an object.</summary>
    private static bool Survives(LayerItem item, Point2D point)
    {
        foreach (ClipSpec clip in ClipsOn(item))
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
