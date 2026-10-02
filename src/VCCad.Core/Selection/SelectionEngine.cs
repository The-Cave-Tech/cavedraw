using VCCad.Geometry;
using VCCad.Core.Model;
using VCCad.Core.Picking;

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
    /// The affine map from an item's **placement frame** into its artboard's frame: every enclosing
    /// group's <see cref="ArtGroup.Transform"/>, composed outermost last.
    ///
    /// This is the composition <c>PdfDocumentExporter</c> performs as its walk descends
    /// (<c>childToDoc = toDoc.Compose(group.Transform)</c>), stated once so that the canvas, the
    /// selection engine and the exporter cannot come to three different answers about where
    /// transformed artwork is. A group's transform is a local→parent map, so walking up from an item
    /// and pre-composing each ancestor gives parent∘…∘innermost, which is the frame the item is
    /// drawn in once every enclosing group has had its say.
    ///
    /// "Placement frame" rather than "own frame" because a group is placed in the space its own
    /// coordinates are written in while its children are in the space its transform establishes -
    /// the distinction issue #159 turns on.
    /// </summary>
    public static AffineTransform ToArtboard(LayerItem item)
    {
        AffineTransform transform = AffineTransform.Identity;

        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        return transform;
    }

    /// <summary>
    /// The affine map from an item's placement frame into document/world coordinates - the artboard
    /// origin composed on the outside of <see cref="ToArtboard"/>, which is where the canvas draws.
    ///
    /// The composition itself is <see cref="CadDocument.ToWorld"/>, which the layout frame
    /// (<see cref="InArtboard"/>) and every command that reparents art also share. Stated once there
    /// rather than per caller, so a gesture, a panel and a command cannot come to three different answers
    /// about which frame an item's numbers are written in (#174).
    /// </summary>
    public static AffineTransform ToWorld(LayerItem item)
        => item.Document is { } document ? document.ToWorld(item) : ToArtboard(item);

    /// <summary>
    /// The affine map from world coordinates back into an item's **placement frame** - the frame its
    /// own geometry is stored in. The exact inverse of <see cref="ToWorld"/>, or null when the frame
    /// collapses the plane and so has none.
    ///
    /// This is the same composition <see cref="ToWorld"/> states, written the other way round, and it
    /// exists because an **editing gesture happens in world coordinates while the geometry it changes is
    /// stored here**. A drag of d screen points has to become d carried into this frame, or a group with
    /// `scale(2)` moves its contents twice as far as the pointer and a rotated group moves them the wrong
    /// way - the picture, the hit test and the edit disagreeing about a frame (#165).
    ///
    /// Null rather than an identity: a group that collapses the plane paints nothing and picks nothing,
    /// so there is no honest answer to "where does this go" and guessing one would invent a position.
    /// </summary>
    public static AffineTransform? FromWorld(LayerItem item)
    {
        AffineTransform toWorld = ToWorld(item);
        return toWorld.IsInvertible ? toWorld.Inverted() : null;
    }

    /// <summary>
    /// A displacement in an item's own placement frame that appears as <paramref name="worldDelta"/>
    /// in world coordinates, or zero when the frame collapses the plane.
    ///
    /// A displacement is carried by the linear part alone, so the artboard origin - which only shifts
    /// the frame - drops out of the answer. This is the whole conversion an editing gesture needs: the
    /// pointer moved <paramref name="worldDelta"/>, and the stored geometry must move by this, not by
    /// the world delta itself.
    /// </summary>
    public static Vector2D DeltaInItem(LayerItem item, Vector2D worldDelta)
        => FromWorld(item) is { } fromWorld ? fromWorld.Transform(worldDelta) : default;

    /// <summary>
    /// A transform stated in **world** coordinates as it acts on geometry stored in an item's own
    /// placement frame: <c>FromWorld ∘ world ∘ ToWorld</c>, or null when the frame collapses the plane.
    ///
    /// <see cref="DeltaInItem"/> carries a **displacement** across, which is enough for a translation
    /// because the linear part of the frame cancels. A scale or a rotation does not cancel: a group that
    /// turns its contents a quarter turn maps a world x-scale onto the item's own y axis, and a world
    /// mirror onto the item's other mirror axis. The honest answer is therefore the whole affine
    /// conjugated by the frame, which is exact in any invertible frame and is one rule rather than a
    /// family of special cases - the composition <see cref="ToWorld"/> states, read the other way round
    /// (#165, #172, #173).
    ///
    /// Null rather than the identity, for the reason <see cref="FromWorld"/> gives: an item whose frame
    /// collapses the plane is painted nowhere a pointer could reach, so "where does this go" has no answer.
    /// </summary>
    public static AffineTransform? InItemFrame(LayerItem item, AffineTransform world)
        => FromWorld(item) is { } fromWorld
            ? fromWorld.Compose(world).Compose(ToWorld(item))
            : null;

    /// <summary>
    /// The uniform scale an affine transform applies, √|det|.
    ///
    /// Used to move the pick tolerance into whichever frame a point is being tested in: three document
    /// units is three units of screen, so inside a group that doubles everything it is one and a half
    /// units of the group's own space.
    /// </summary>
    private static double ScaleOf(AffineTransform transform)
        => Math.Sqrt(Math.Abs(transform.Determinant));

    /// <summary>
    /// How far the pointer may wander and still count as a click rather than a marquee.
    ///
    /// A press and release in the same place is a click; anything further is a drag, whether
    /// or not the person meant one. Without a slop of some kind, every click that shakes by a
    /// pixel would become a marquee over nothing and select nothing.
    /// </summary>
    public const double ClickSlop = 2.0;

    /// <summary>
    /// Plays a gesture and says what it selected.
    ///
    /// This is the whole surface: a document and a list of what the pointer did. A test can
    /// write the list by hand, or save one an automation run produced and replay it later with
    /// no window open - which is the point, because a regression suite that needs the
    /// application running is one that stops being run.
    ///
    /// A press and release in the same place is a click. Anything else is a marquee, and the
    /// marquee is a path, so the rectangle one and the lasso one differ only in the points they
    /// hand over.
    /// </summary>
    /// <param name="focused">The artboard already focused when the gesture began.</param>
    public static SelectionResult Play(
        CadDocument document, IEnumerable<SelectEvent> events, Artboard? focused = null)
    {
        Point2D? press = null;
        var path = new List<Point2D>();

        foreach (SelectEvent e in events)
        {
            switch (e)
            {
                case PointerDown down:
                    press = down.Point;
                    path.Clear();
                    break;

                case PointerMove move when press is not null:
                    path.Add(move.Point);
                    break;

                case PointerUp up when press is { } from:
                    press = null;

                    bool clicked = path.Count == 0 &&
                                   Math.Abs(up.Point.X - from.X) <= ClickSlop &&
                                   Math.Abs(up.Point.Y - from.Y) <= ClickSlop;

                    return clicked
                        ? Click(document, from, focused)
                        : ByLasso(document, from, path, focused);
            }
        }

        // A gesture that never came up changed nothing.
        return SelectionResult.Empty;
    }

    /// <summary>
    /// What a freehand path selects.
    ///
    /// The path closes with a straight segment from its last point back to its first - the one
    /// the pointer went down at - so the region is a closed shape however wildly it was drawn.
    /// A rectangular marquee is the same call with four corners.
    /// </summary>
    public static SelectionResult ByLasso(
        CadDocument document, Point2D start, IReadOnlyList<Point2D> path, Artboard? focused = null)
    {
        // The press point is the first corner and the path is everything after it. Leaving it
        // out lost the corner the person started at, so a four-corner drag became a triangle
        // over the wrong three points and a short lasso became nothing at all.
        //
        // Fewer than three corners is not a lasso, it is a rectangle: a press and a single move
        // is what an ordinary marquee drag looks like, and it has to select the same region a
        // rectangle between those two points would.
        IReadOnlyList<Point2D> corners = path.Count switch
        {
            0 => new[] { start, start },
            1 => MarqueePath(start, path[0]).Points,
            _ => new[] { start }.Concat(path).ToList(),
        };

        var shape = new Polygon(corners);
        Artboard? startedIn = ArtboardAt(document.Artboards, start) ?? focused;

        return startedIn is not null
            ? WithinArtboard(startedIn, shape)
            : AcrossArtboards(document, shape);
    }

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

        LayerItem? hit = Nearest(board, point);
        return hit is null
            ? new SelectionResult(Array.Empty<LayerItem>(), Array.Empty<Artboard>(), board)
            : new SelectionResult(new[] { hit }, Array.Empty<Artboard>(), board);
    }

    /// <summary>
    /// The object of an artboard under a point, or null.
    ///
    /// **The deepest object wins, not the outermost.** A group is a container, not a thing you click: a
    /// person clicking a pattern piece means the piece, and answering with the group that holds two hundred
    /// of them makes every piece unselectable. That is not hypothetical - it is what happened the moment
    /// the importer started reproducing the file's grouping, and it read as "I can't select anything on the
    /// page except the red text", because the text happened to sit outside the group.
    ///
    /// A group is still reachable: by its name in the layers panel, and by clicking where the group itself
    /// is painted - its own mask, or the area its children do not cover.
    /// </summary>
    public static LayerItem? Within(Artboard artboard, Point2D point)
    {
        IReadOnlyList<LayerItem> chain = Chain(artboard, point);
        return chain.Count == 0 ? null : chain[^1];
    }

    /// <summary>
    /// Every object under a point, outermost first, ending at the deepest.
    ///
    /// This is what a click drills through. The first entry is what a single click selects, and each
    /// double-click moves one further along - so "one level down" means the child under the pointer rather
    /// than whichever sibling happens to be nearest, which is the difference between drilling into a
    /// hierarchy and wandering around it.
    /// </summary>
    public static IReadOnlyList<LayerItem> Chain(Artboard artboard, Point2D point)
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

        if (topmost is null)
        {
            return Array.Empty<LayerItem>();
        }

        var chain = new List<LayerItem> { topmost };
        Descend(topmost, local, chain);
        return chain;
    }

    /// <summary>Walks into the group under the point, front to back, appending what it finds.</summary>
    private static void Descend(LayerItem item, Point2D point, List<LayerItem> chain)
    {
        if (item is not ArtGroup group)
        {
            return;
        }

        // A group's children are in the space its own transform establishes, not in the space the group
        // itself is placed in, so the point is carried into that space once and every child is tested
        // there. The tolerance travels with it: three document units of reach is smaller inside a group
        // that enlarges what it holds.
        if (!group.Transform.IsInvertible)
        {
            // The group collapses the plane it holds, so nothing inside it is painted anywhere a pointer
            // could land. Reported by the hit test as "nothing here" rather than mapped through a
            // transform that has no inverse.
            return;
        }

        Point2D local = group.Transform.Inverted().Transform(point);
        double tolerance = PickTolerance / ScaleOf(group.Transform);

        for (int i = group.Children.Count - 1; i >= 0; i--)
        {
            LayerItem child = group.Children[i];
            if (Hits(child, local, tolerance))
            {
                chain.Add(child);
                Descend(child, local, chain);
                return;
            }
        }
    }

    /// <summary>
    /// What a click selects from a chain: the outermost object, or - on a double-click - one level further
    /// down than whatever in the chain is already selected.
    ///
    /// A double-click with nothing from this chain selected starts at the top, which is what a double-click
    /// on a fresh object means. At the bottom it stays there: the drill ends at the object rather than
    /// wrapping round or clearing the selection.
    /// </summary>
    public static LayerItem? Drill(
        IReadOnlyList<LayerItem> chain, IReadOnlyList<LayerItem> selected, int clickCount)
    {
        if (chain.Count == 0)
        {
            return null;
        }

        if (clickCount < 2)
        {
            return chain[0];
        }

        int at = -1;
        for (int i = 0; i < chain.Count; i++)
        {
            if (selected.Contains(chain[i]))
            {
                at = i;
            }
        }

        return chain[Math.Min(at + 1, chain.Count - 1)];
    }


    /// <summary>
    /// The object of an artboard nearest the point, within the pick tolerance.
    ///
    /// Nearest, not merely "near enough". Two objects can both be within the tolerance of a
    /// click - a line passing close to a shape, a label beside a border - and the one the
    /// person meant is the one they are closest to. A tie goes to whichever is on top, since
    /// that is the one they can see.
    /// </summary>
    public static LayerItem? Nearest(Artboard artboard, Point2D point, double? tolerance = null)
    {
        double limit = tolerance ?? PickTolerance;
        Point2D local = point - new Vector2D(artboard.X, artboard.Y);
        LayerItem? nearest = null;
        double best = double.MaxValue;

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsEffectivelyVisible)
            {
                continue;
            }

            foreach (LayerItem item in layer.Children)
            {
                if (!item.IsEffectivelyVisible() || item.IsLocked)
                {
                    continue;
                }

                double distance = DistanceTo(item, local);
                if (distance > limit)
                {
                    continue;
                }

                // A later item is painted above, so it wins an equal distance.
                if (distance <= best)
                {
                    best = distance;
                    nearest = item;
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// How far a point is from an object's visible geometry, or infinity when it has none.
    ///
    /// A PATH is measured to its outline. Measuring to the filled region instead made a large panel
    /// swallow every click over the lines and labels drawn on top of it: the panel contains the
    /// point, so its distance was zero and it beat whatever the pointer was actually on. What a
    /// person aims at is the artwork - the paths - not the area a fill happens to cover.
    ///
    /// Text and images have no outline to aim at, so their box is their geometry.
    ///
    /// Clipping is still respected: a path a parent has cut away where the point is has no geometry
    /// there, whatever its own outline does, so a click beside where it used to be picks nothing.
    ///
    /// Note there is no "is it empty" guard for an open path. A line is two points and cannot
    /// contain anything, so it reads as empty - but it is still a line, and a line is exactly the
    /// thing a person expects to be able to click. Guarding on emptiness made every open path
    /// unpickable.
    /// </summary>
    public static double DistanceTo(LayerItem item, Point2D point)
    {
        if (item is PathItem path)
        {
            if (ClipsOn(path).Count > 0 && !Survives(path, point))
            {
                return double.MaxValue;
            }

            return PathPicking.OutlineDistance(path, point);
        }

        return VisibleRegion(item, default).DistanceTo(point);
    }

    /// <summary>
    /// The region of an object that is actually there, after every clip on it.
    ///
    /// This is what makes clipping a selection concern: a rectangle cut down to a sliver is
    /// only that sliver, so a marquee that encloses the sliver encloses the object and one
    /// that encloses only the cut-away part does not. Non-rectangular clips count - the
    /// intersection is taken against the clip's own outline, not its box.
    ///
    /// Clips are recorded in the frame the item they cut is **placed** in (a group's own transform
    /// maps its children into that space rather than moving the outline out of it), so each item's
    /// own clips are applied before it is carried up by the groups around it.
    /// </summary>
    public static Polygon VisibleRegion(LayerItem item, Vector2D offset)
    {
        Polygon region = Place(item);

        if (region.IsEmpty)
        {
            return region;
        }

        // Up into the artboard, then out to document coordinates - the order the exporter's own
        // `toDoc` walks in, which is what keeps a marquee and a rendered page about the same picture.
        AffineTransform toArtboard = ToArtboard(item);
        if (toArtboard != AffineTransform.Identity)
        {
            region = Mapped(region, toArtboard);
        }

        return offset.X == 0 && offset.Y == 0
            ? region
            : Mapped(region, AffineTransform.CreateTranslation(offset.X, offset.Y));
    }

    /// <summary>An item's painted outline and its own clips, in the frame the item is placed in.</summary>
    private static Polygon Place(LayerItem item)
    {
        Polygon region = Outline(item);

        if (region.IsEmpty)
        {
            return region;
        }

        foreach (ClipSpec clip in item.Clips)
        {
            var rings = new List<IEnumerable<Point2D>>();

            foreach (SubPath sub in clip.SubPaths)
            {
                rings.Add(Flatten(sub, default).Points);
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

    /// <summary>
    /// An object's own outline in the frame it is placed in: its geometry, or - for a group - its
    /// children's regions carried up by the group's own transform.
    /// </summary>
    private static Polygon Outline(LayerItem item)
    {
        var rings = new List<IEnumerable<Point2D>>();

        switch (item)
        {
            case PathItem path:
                foreach (SubPath sub in path.SubPaths)
                {
                    rings.Add(Flatten(sub, default).Points);
                }

                break;

            case TextItem text:
                rings.Add(Box(text.BoundingBox(), default));
                break;

            case ImageItem image:
                rings.Add(Box(image.Placement, default));
                break;

            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    // The child is placed in the group's own space, so its region is mapped into that
                    // space - mapped, not offset, because the transform may turn or scale it.
                    Polygon childRegion = Place(child);
                    if (!childRegion.IsEmpty)
                    {
                        rings.Add(Mapped(childRegion, group.Transform).Points);
                    }
                }

                break;
        }

        return new Polygon(rings);
    }

    /// <summary>A polygon with every ring carried through an affine transform.</summary>
    private static Polygon Mapped(Polygon polygon, AffineTransform transform)
        => new(polygon.Rings.Select(ring => ring.Select(transform.Transform)));

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
    ///
    /// A line is a two-point ring and has no area, so it reads as an empty region - but it is
    /// still geometry, and a marquee over it must select it. Clicking a line has always worked
    /// (see <see cref="DistanceTo"/>, which explicitly does not guard on emptiness); a marquee
    /// that did guard on it selected everything except the lines.
    /// </summary>
    private static bool Enclosed(LayerItem item, Vector2D offset, Polygon path)
    {
        if (!item.IsEffectivelyVisible() || item.IsLocked)
        {
            return false;
        }

        return VisibleRegion(item, offset).IsEnclosedBy(path);
    }

    /// <summary>
    /// Where objects belong when they are dropped at a point: an artboard's layer, or the
    /// pasteboard.
    ///
    /// Dragging something off the page it started on is how a person moves it between pages,
    /// and where it lands decides which page now owns it. A point outside every artboard means
    /// the pasteboard, which is a place objects can live rather than a place they are lost.
    /// </summary>
    public static IItemContainer ContainerAt(CadDocument document, Point2D point)
    {
        Artboard? board = ArtboardAt(document.Artboards, point);

        if (board is null)
        {
            return document.Orphans;
        }

        return board.Layers.FirstOrDefault(l => l.IsEffectivelyVisible)
            ?? board.Layers.FirstOrDefault()
            ?? (IItemContainer)document.Orphans;
    }

    /// <summary>
    /// Whether items should be rehomed after being translated, and to what, or null to leave
    /// them where they are.
    ///
    /// <paramref name="pointer"/> is where the pointer is - the honest answer during a drag.
    /// A translation that arrives through the API has no pointer, so there is nothing to
    /// substitute for it but the transformation itself: the vector's END, with its origin at
    /// the CENTRE OF THE SELECTION. That is what makes a scripted move behave the way the same
    /// move by hand does, which is the point of the parity rule - an operation and a gesture
    /// must not disagree about where an object ends up.
    /// </summary>
    /// <param name="pointer">Where the pointer is, when there is one.</param>
    /// <param name="centreBefore">The selection's centre before the move.</param>
    /// <param name="delta">The translation applied.</param>
    public static IItemContainer? RehomeTarget(
        CadDocument document,
        IReadOnlyList<LayerItem> items,
        Point2D? pointer = null,
        Point2D? centreBefore = null,
        Vector2D? delta = null)
    {
        if (items.Count == 0)
        {
            return null;
        }

        Point2D deciding = pointer ??
            (centreBefore is { } centre && delta is { } move
                ? centre + move
                : CentreOf(items));

        IItemContainer target = ContainerAt(document, deciding);

        // Already there: nothing to do, and saying so keeps this from rebuilding the tree on
        // every move that stays where it belongs.
        //
        // Compared by the PAGE, not by the container. A container-identity test says "an object inside a
        // group is not on the group's layer", so any translation that stayed on its own page tore the
        // object out of its group and dropped the group's transform on the way - which is how
        // `object.move` left the artwork somewhere neither the file nor the identical drag put it (#172).
        // The repository's own wording for this rule is "an object dragged within its own page stays put",
        // and a page is the artboard.
        //
        // Both frames being the pasteboard is the same place too, so a loose object moved around the
        // pasteboard is left alone.
        return ReferenceEquals(ArtboardOf(items[0].Container), ArtboardOf(target)) ? null : target;
    }

    /// <summary>
    /// The artboard a container belongs to, or null for the pasteboard. Walked rather than read off the
    /// container so a group, a layer and a detached item all answer the same kind of thing.
    /// </summary>
    private static Artboard? ArtboardOf(IItemContainer? container)
    {
        for (IItemContainer? c = container; c is not null; c = (c as LayerItem)?.Container)
        {
            if (c is Layer layer)
            {
                return layer.Artboard;
            }
        }

        return null;
    }

    /// <summary>
    /// The combined bounds of a selection in **document/world** coordinates, every enclosing group's
    /// transform composed in.
    ///
    /// Stated here for the same reason <see cref="ToWorld"/> is: a document position that is going to be
    /// turned into a translation has to be measured in the frame the translation is in, and
    /// <see cref="PathItem.WorldBounds"/> answers a narrower question - it adds the artboard origin and
    /// ignores the groups above the path. On a grouped object the two answers differ by the group
    /// transform, so an absolute position expressed against the narrow one lands somewhere the caller
    /// never asked for (#172).
    /// </summary>
    public static Rect2D WorldBounds(IReadOnlyList<LayerItem> items)
    {
        Rect2D box = Rect2D.Empty;

        foreach (LayerItem item in items)
        {
            box = box.Union(BoundsOf(item));
        }

        return box;
    }

    /// <summary>The centre of a selection's combined bounds, in document coordinates.</summary>
    public static Point2D CentreOf(IReadOnlyList<LayerItem> items)
    {
        bool any = false;
        double left = 0, top = 0, right = 0, bottom = 0;

        foreach (LayerItem item in items)
        {
            Rect2D box = BoundsOf(item);

            if (!any)
            {
                left = box.Left;
                top = box.Top;
                right = box.Right;
                bottom = box.Bottom;
                any = true;
                continue;
            }

            left = Math.Min(left, box.Left);
            top = Math.Min(top, box.Top);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }

        return any ? new Point2D((left + right) / 2, (top + bottom) / 2) : default;
    }

    /// <summary>
    /// An item's bounds in the **artboard frame** - the frame the document stores its coordinates in: the
    /// item's own placement frame carried up through every enclosing group, with the artboard origin left
    /// off.
    ///
    /// This is the frame the layout operations measure in, and stating it here is the fix for #174. Laying
    /// objects out means comparing where they are with each other, and two objects written into different
    /// frames have no common scale to be compared on: `ItemBounds.Of` answers about one object's own
    /// placement frame - a group's transform and no ancestors - so a selection spanning a group boundary
    /// produced bounds in different spaces and a delta that was wrong for both of them.
    ///
    /// The artboard frame rather than world coordinates because that is what the document stores, so a
    /// displacement measured in it is one small conversion away from the geometry, and because the
    /// artboard origin cancels out of a *difference* anyway. <see cref="WorldBounds"/> remains the answer
    /// for an absolute position that has to be expressed in the frame the canvas draws in.
    /// </summary>
    public static Rect2D InArtboard(LayerItem item) => FrameBounds(item, artboard: true);

    /// <summary>An object's bounds in document coordinates, every enclosing group's transform composed in.</summary>
    private static Rect2D BoundsOf(LayerItem item) => FrameBounds(item, artboard: false);

    /// <summary>
    /// The item's box carried into its placement frame by the groups above it, and then - when
    /// <paramref name="artboard"/> - by the artboard origin its coordinates are stored relative to.
    ///
    /// The two frames differ only by that last translation, and they are computed together so they cannot
    /// drift: a group's box is in its own local space, which its own transform carries into the frame it is
    /// placed in, while every other item is already in the frame it is placed in.
    /// </summary>
    private static Rect2D FrameBounds(LayerItem item, bool artboard)
    {
        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            ImageItem image => image.Placement,
            ArtGroup group when group.Children.Count > 0 => group.BoundingBox(),
            _ => Rect2D.Empty,
        };

        AffineTransform placement = artboard ? ToArtboard(item) : ToWorld(item);
        AffineTransform frame = item is ArtGroup own
            ? placement.Compose(own.Transform)
            : placement;

        return frame.Transform(box);
    }

    /// <summary>
    /// What a transform should actually be applied to.
    ///
    /// By default a transform reaches the children: moving a group moves what is in it, which
    /// is what a group is for. Holding the platform modifier says "this object only" - the
    /// group's own placement moves and its contents stay where they are - which is how a
    /// container is nudged without disturbing a drawing placed carefully inside it.
    ///
    /// Command on macOS and Control elsewhere, which is the platform's own convention rather
    /// than a choice of ours.
    /// </summary>
    /// <param name="selection">The selected objects.</param>
    /// <param name="ownOnly">Whether the platform modifier is held.</param>
    public static IReadOnlyList<LayerItem> TransformTargets(
        IReadOnlyList<LayerItem> selection, bool ownOnly)
    {
        if (ownOnly)
        {
            return selection.ToList();
        }

        var all = new List<LayerItem>();

        foreach (LayerItem item in selection)
        {
            if (!all.Contains(item))
            {
                all.Add(item);
            }

            foreach (LayerItem descendant in Descendants(item))
            {
                if (!all.Contains(descendant))
                {
                    all.Add(descendant);
                }
            }
        }

        return all;
    }

    /// <summary>The key that means "this object only" on the platform we are running on.</summary>
    public static bool IsOwnTransformModifier => OperatingSystem.IsMacOS();

    /// <summary>Everything inside an object: its children, their children, and so on.</summary>
    public static IEnumerable<LayerItem> Descendants(LayerItem item)
    {
        if (item is not ArtGroup group)
        {
            yield break;
        }

        foreach (LayerItem child in group.Children)
        {
            yield return child;

            foreach (LayerItem nested in Descendants(child))
            {
                yield return nested;
            }
        }
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
    ///
    /// <paramref name="point"/> is in the frame the item is **placed** in - the frame its own geometry
    /// is written in for a path, text or image, and the frame a group's coordinates are written in for a
    /// group. A group's children are in the space its transform establishes, so the point is carried
    /// into that space before they are tested.
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
            ArtGroup group => ChildrenHit(group, point, tolerance),
            _ => false,
        };
    }

    /// <summary>Whether any child of a group is under a point given in the group's placement frame.</summary>
    private static bool ChildrenHit(ArtGroup group, Point2D point, double tolerance)
    {
        // Nothing inside a group that collapses the plane is painted anywhere a pointer can land, and
        // its transform has no inverse to carry the point in with. Reported as a miss rather than
        // guessed at through a matrix that does not exist.
        if (!group.Transform.IsInvertible)
        {
            return false;
        }

        Point2D local = group.Transform.Inverted().Transform(point);
        double localTolerance = tolerance / ScaleOf(group.Transform);

        return group.Children.Any(child => Hits(child, local, localTolerance));
    }

    /// <summary>
    /// Every clip that applies to an object: its own, and those of the groups above it.
    ///
    /// A clipping mask clips what is inside it, not merely itself, so a shape nested two
    /// groups deep is bounded by both. Without walking up, a child looked unclipped however
    /// tightly its parents held it - which is the other half of the report.
    ///
    /// The frames differ: each clip is recorded in the frame its own container is *placed* in, so
    /// this is the list of clips and not a set of outlines in one space. <see cref="Survives"/>
    /// carries the point into each frame before asking.
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

    /// <summary>
    /// Whether a point survives every clip that applies to an object.
    ///
    /// The item's own clips were recorded in the frame the item is placed in, which is the frame the
    /// point arrives in. An ancestor's clip was recorded in <em>that ancestor's</em> placement frame, so
    /// the point is carried up into each ancestor's frame in turn - testing the whole list against one
    /// point was right only while every group between them had the identity for a transform.
    /// </summary>
    private static bool Survives(LayerItem item, Point2D point)
    {
        foreach (ClipSpec clip in item.Clips)
        {
            if (!clip.Contains(point))
            {
                return false;
            }
        }

        if (item.Container is null)
        {
            return true;
        }

        Point2D inArtboard = ToArtboard(item).Transform(point);

        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is not LayerItem ancestor || ancestor.Clips.Count == 0)
            {
                continue;
            }

            AffineTransform above = ToArtboard(ancestor);
            if (!above.IsInvertible)
            {
                // The ancestor's own frame is collapsed, so its clip cannot be asked a question with an
                // answer: nothing of what it holds is anywhere a pointer could land.
                return false;
            }

            Point2D inFrame = above.Inverted().Transform(inArtboard);

            foreach (ClipSpec clip in ancestor.Clips)
            {
                if (!clip.Contains(inFrame))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a point is on a path, for the purpose of being clicked.
    ///
    /// The rule is written around the one case that was actually broken. Page 1 of the LILLIE sample has an
    /// **unfilled, stroked, page-sized frame** (540x720) as the topmost object in the pattern group, and a
    /// box-based test made it swallow every click on the page - the report was "it registers as though I'm
    /// clicking the bounding rectangle", and that is exactly what the code did.
    ///
    /// So: a path that is **stroked and not filled** exists only along its stroke - that is the frame, and
    /// it is why this function exists. Everything else keeps the box-based answer it always had, because
    /// tightening that too was a mistake worth recording: judging a filled path by its real outline makes
    /// the inside of a hollow piece unclickable, and a person clicking the middle of a pattern piece means
    /// the piece. A refinement that costs selectability is not a refinement.
    /// </summary>
    private static bool PathHits(PathItem path, Point2D point, double tolerance)
    {
        bool inBox = path.SubPaths.Any(sub =>
            !sub.BoundingBox().IsEmpty && sub.BoundingBox().Inflated(tolerance).Contains(point));

        if (path.Fill.IsVisible)
        {
            return inBox;
        }

        if (!path.HasVisibleStroke)
        {
            // Nothing is painted, so nothing is there to click. An invisible object that still swallowed
            // clicks would be the same defect as the frame, one step quieter.
            return false;
        }

        // Stroked and unfilled: the stroke is the object. Half the width of the **widest visible stroke**,
        // because that is how far the paint reaches from the centreline - a path whose second stroke is wider
        // is clickable that much further out, and one that consulted only the bottom stroke would not be.
        double reach = tolerance + (path.Strokes.Where(s => s.HasVisibleOutline).Max(s => s.Width) / 2.0);
        foreach (SubPath sub in path.SubPaths)
        {
            if (NearSubPath(sub, point, reach))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a point is within <paramref name="reach"/> of a subpath's own drawn line.</summary>
    private static bool NearSubPath(SubPath sub, Point2D point, double reach)
    {
        int segments = sub.SegmentCount;
        if (segments == 0)
        {
            return sub.Nodes.Count == 1 && Distance(point, sub.Nodes[0].Anchor) <= reach;
        }

        for (int i = 0; i < segments; i++)
        {
            CubicBezier curve = sub.GetSegment(i);

            // Sampled with enough steps that no step is longer than the reach, so a segment cannot be
            // stepped over: a straight segment needs one step, a long curve gets as many as it needs.
            double length = curve.EstimateLength();
            int steps = Math.Clamp((int)Math.Ceiling(length / Math.Max(reach, 0.5)), 1, 256);

            Point2D previous = curve.PointAt(0);
            for (int step = 1; step <= steps; step++)
            {
                Point2D next = curve.PointAt((double)step / steps);
                if (DistanceToSegment(point, previous, next) <= reach)
                {
                    return true;
                }

                previous = next;
            }

            foreach (PathNode node in sub.Nodes)
            {
                if (Distance(point, node.Anchor) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double DistanceToSegment(Point2D point, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            return Distance(point, a);
        }

        double t = (((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        return Distance(point, new Point2D(a.X + (t * dx), a.Y + (t * dy)));
    }
}
