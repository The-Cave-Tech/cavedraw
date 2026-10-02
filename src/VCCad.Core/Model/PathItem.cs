using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A styled drawing primitive: one or more <see cref="SubPath"/>s plus a fill and
/// stroke. A path object is the smallest independently addressable artwork — this
/// is what the user clicks, styles, and moves.
///
/// Geometric truth lives in the subpaths; the styling record describes how the
/// outline is painted:
/// <list type="bullet">
/// <item><see cref="Stroke"/> — always applicable (open and closed paths).</item>
/// <item><see cref="Fill"/> — per the brief, closed paths are filled with the fill
/// colour. The fill spec is retained even while the path is open so that closing
/// it later paints immediately without re-picking a colour.</item>
/// </list>
/// Coordinates are artboard-local (not transformed by any ancestor group).
/// </summary>
public sealed class PathItem : LayerItem
{
    private FillSpec _fill = FillSpec.None;
    private readonly List<StrokeSpec> _strokes = new() { StrokeSpec.Hairline(ColorRgb.Black) };
    private double _opacity = 1.0;

    /// <summary>Subpaths in draw order; usually one, but compound shapes use many.</summary>
    /// <summary>
    /// The shape this path was made as, or null when it is an ordinary path.
    ///
    /// The path is the geometry that paints; this is what makes it a *shape* - the parameters the
    /// control handles move, and the symmetry a segment edit follows. It is deliberately nullable and
    /// deliberately clearable: a shape whose nodes have been edited away from its definition has to
    /// stop claiming to be one, or the handles would describe something that is no longer there.
    /// </summary>
    public ShapeDefinition? Shape { get; set; }
    public List<SubPath> SubPaths { get; } = new();

    /// <summary>Incremented whenever the geometry changes. Renderers cache compiled
    /// geometry against this value so they don't rebuild it every frame.</summary>
    public int GeometryRevision { get; private set; }

    /// <summary>Marks the geometry as changed (bumps the revision and notifies).</summary>
    private void TouchGeometry()
    {
        GeometryRevision++;
        NotifyPropertyChanged(nameof(SubPaths));
    }

    /// <summary>
    /// Announces that the geometry changed in place, for edits that write nodes directly rather than
    /// replacing the subpaths. Without it a renderer's cached geometry goes stale - which is exactly
    /// what the symmetric segment edit does when it bows ten segments at once.
    /// </summary>
    public void GeometryChanged() => TouchGeometry();
    /// <summary>The fill paint. See class remarks for the open/closed rule.</summary>
    public FillSpec Fill
    {
        get => _fill;
        set => SetField(ref _fill, value);
    }

    /// <summary>
    /// The strokes painted on this path, <b>bottom to top</b>.
    ///
    /// A path carries an ordered stack rather than one stroke, because that is what a person draws with: an
    /// outline under a highlight, a wide stroke under a narrow one. The list is never empty, so callers can read
    /// it without a null check: a new path holds the classic hairline, and a path with no visible stroke holds a
    /// single <see cref="StrokeSpec.None"/>. The ordinary path is a stack of one.
    /// </summary>
    public List<StrokeSpec> Strokes => _strokes;

    /// <summary>
    /// The bottom stroke. Reading this is the <b>compatibility behaviour, not the correct one</b>: a path with
    /// two strokes needs every one of them painted, exported, scaled and dumped, and a caller that reads this
    /// gets one of them.
    ///
    /// It exists so the model could gain a stack without every one of its ninety-odd call sites changing in the
    /// same commit. The call sites that matter read <see cref="Strokes"/>. Setting it replaces the whole stack
    /// with that single stroke, which is what "set the stroke" means on a path that has one.
    /// </summary>
    public StrokeSpec Stroke
    {
        get => _strokes.Count > 0 ? _strokes[0] : StrokeSpec.None;
        set
        {
            if (_strokes.Count == 1 && _strokes[0] == value)
            {
                return;
            }

            _strokes.Clear();
            _strokes.Add(value);
            NotifyStrokesChanged();
        }
    }

    /// <summary>
    /// Announces that the stroke stack changed.
    ///
    /// A list cannot raise a notification, so code that adds, removes or reorders a stroke through
    /// <see cref="Strokes"/> directly has to call this. Without it a panel or a renderer that caches against
    /// the stack keeps showing the old one.
    /// </summary>
    public void NotifyStrokesChanged()
    {
        NotifyPropertyChanged(nameof(Stroke));
        NotifyPropertyChanged(nameof(Strokes));
    }

    /// <summary>Whether any stroke on this path has a visible outline.</summary>
    public bool HasVisibleStroke => _strokes.Any(s => s.HasVisibleOutline);

    /// <summary>
    /// The live path effect this path carries, as the file described it, or null when it has none.
    ///
    /// This is the **description**, not a translation of it: Inkscape's effect name, id, version and every parameter
    /// the element carried. The converted result is <see cref="StrokeSpec.WidthProfile"/>, which is what draws - and
    /// it is a *derived* value, so it goes stale the moment the geometry it was derived from changes. A powerstroke
    /// stores its knots as a segment index over the whole path, so the same element means 0.5 on a two-segment path
    /// and 0.25 on a four-segment path; a path that keeps only the converted profile keeps the old number forever
    /// (issue #180).
    ///
    /// Keeping the description is what makes re-derivation possible without re-reading the file, and it is the same
    /// move <see cref="ArtGroup.SourceId"/> makes for `use`: the picture is right in a single render either way, and
    /// the link is what lets an edit reach what it was derived from. The element itself still travels verbatim in
    /// <see cref="LayerItem.ForeignElements"/>, because that is what an export writes back;
    /// <see cref="Commands.RefreshPathEffectsCommand"/> is the step that honours this.
    /// </summary>
    public PathEffectSpec? PathEffect { get; set; }

    /// <summary>Whole-object opacity in [0,1]; the layer and artboard apply on top.</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, MathUtils.Clamp(value, 0.0, 1.0));
    }

    /// <summary>True when every subpath is closed (and at least one exists).</summary>
    public bool IsFullyClosed => SubPaths.Count > 0 && SubPaths.All(sp => sp.IsClosed);

    /// <summary>Appends a subpath and returns it for node building.</summary>
    public SubPath AddSubPath(bool closed)
    {
        var sp = new SubPath { IsClosed = closed };
        SubPaths.Add(sp);
        TouchGeometry();
        return sp;
    }

    /// <summary>The owning artboard's origin. Path coordinates are stored relative
    /// to it; add this to reach document/world space.</summary>
    public Vector2D ArtboardOffset()
    {
        Artboard? artboard = OwningLayer()?.Artboard;
        return artboard is null ? default : new Vector2D(artboard.X, artboard.Y);
    }

    /// <summary>Bounding box in document/world space (local bounds + artboard origin).</summary>
    public Rect2D WorldBounds()
    {
        Rect2D box = BoundingBox();

        // A degenerate box is still geometry. A horizontal line is a box of zero height and
        // returning it without the artboard origin put the line at the wrong place on any
        // page not sitting at the document origin. Only a path with no nodes at all has no
        // world position to report, and that is a question about nodes, not about area.
        if (!SubPaths.Any(sp => sp.Nodes.Count > 0))
        {
            return box;
        }

        Vector2D offset = ArtboardOffset();
        return new Rect2D(box.X + offset.X, box.Y + offset.Y, box.Width, box.Height);
    }

    /// <summary>
    /// Tight bounding box of the whole path in its own coordinate space. For an
    /// empty path (no subpaths) the box is empty.
    /// </summary>
    public Rect2D BoundingBox()
    {
        Rect2D box = Rect2D.Empty;
        foreach (SubPath sp in SubPaths)
        {
            box = box.Union(sp.BoundingBox());
        }

        return box;
    }

    /// <summary>
    /// Deep copy of the path's geometry only (subpaths + nodes); styling is not
    /// copied. Used as the "before/after" snapshot for geometry edit commands.
    /// </summary>
    public PathItem GeometrySnapshot()
    {
        var copy = new PathItem();
        foreach (SubPath sp in SubPaths)
        {
            copy.AddSubPath(sp.IsClosed);
        }

        for (int s = 0; s < SubPaths.Count; s++)
        {
            foreach (PathNode node in SubPaths[s].Nodes)
            {
                copy.SubPaths[s].Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }
        }

        // The definition travels with the snapshot: a snapshot that forgot it would restore a
        // star's nodes and leave the path claiming not to be a shape, which is what an undo across a
        // shape edit used to do.
        copy.Shape = Shape;
        return copy;
    }

    /// <summary>Replaces this path's geometry with a snapshot's geometry (undo/redo).</summary>
    public void RestoreGeometryFrom(PathItem snapshot)
    {
        SubPaths.Clear();
        foreach (SubPath sp in snapshot.SubPaths)
        {
            var copy = AddSubPath(sp.IsClosed);
            foreach (PathNode node in sp.Nodes)
            {
                copy.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }
        }

        // And what the path *is*, not only where its nodes are: restoring geometry without restoring
        // the definition leaves the parameters describing a shape the outline no longer is, which is
        // what an undo across a shape edit used to do.
        Shape = snapshot.Shape;
        TouchGeometry();
    }

    /// <summary>
    /// Rebuilds this path's geometry from its <see cref="Shape"/>. Returns false for an ordinary path,
    /// which is the caller's answer to "was there anything to regenerate".
    /// </summary>
    public bool RegenerateShape()
    {
        if (Shape is null)
        {
            return false;
        }

        Shape.ApplyTo(this);
        return true;
    }

    /// <summary>
    /// Stops this path being a shape and returns the definition it had, leaving the geometry exactly
    /// as it is. This is how a person says "keep this outline, stop keeping it regular".
    /// </summary>
    public ShapeDefinition? DetachShape()
    {
        ShapeDefinition? detached = Shape;
        Shape = null;
        return detached;
    }

    /// <summary>Translates every anchor and handle by <paramref name="delta"/> (in
    /// the path's own coordinate space) — the "move object" geometry operation.</summary>
    public void TranslateGeometryBy(Vector2D delta)
    {
        foreach (SubPath sp in SubPaths)
        {
            foreach (PathNode node in sp.Nodes)
            {
                node.Anchor += delta;
                node.InHandle += delta;
                node.OutHandle += delta;
            }
        }

        TouchGeometry();
    }

    /// <summary>Translates a single node and its two handles by <paramref name="delta"/>.
    /// Handles travel with the anchor so the adjacent curves keep their shape.</summary>
    public void TranslateNode(SubPath sub, int nodeIndex, Vector2D delta)
    {
        if (nodeIndex < 0 || nodeIndex >= sub.Nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        }

        PathNode node = sub.Nodes[nodeIndex];
        node.Anchor += delta;
        node.InHandle += delta;
        node.OutHandle += delta;
        TouchGeometry();
    }

    /// <summary>Rotates every anchor and handle about <paramref name="center"/> by
    /// <paramref name="radians"/> (positive = clockwise on screen, i.e. standard
    /// angle convention applied to model points). Used by the rotation handle.</summary>
    public void RotateGeometryAbout(Point2D center, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        Point2D Rotate(Point2D p)
        {
            double dx = p.X - center.X;
            double dy = p.Y - center.Y;
            return new Point2D(
                center.X + dx * cos - dy * sin,
                center.Y + dx * sin + dy * cos);
        }

        foreach (SubPath sp in SubPaths)
        {
            foreach (PathNode node in sp.Nodes)
            {
                node.Anchor = Rotate(node.Anchor);
                node.InHandle = Rotate(node.InHandle);
                node.OutHandle = Rotate(node.OutHandle);
            }
        }

        TouchGeometry();
    }

    /// <summary>Translates the two end nodes of one segment by <paramref name="delta"/> —
    /// the direct-selection "drag a segment" operation. Nodes move with their
    /// handles so the segment keeps its shape while sliding.</summary>
    public void TranslateSegmentBy(SubPath sub, int segmentIndex, Vector2D delta)
    {
        (int start, int end) = sub.SegmentEndNodes(segmentIndex);
        TranslateNode(sub, start, delta);
        if (end != start)
        {
            TranslateNode(sub, end, delta);
        }
    }

    /// <summary>Scales every anchor and handle about <paramref name="center"/> by
    /// (sx, sy). The reference point stays fixed — the numeric Transform panel's
    /// "resize about the chosen reference point" behaviour.</summary>
    public void ScaleGeometryAbout(Point2D center, double sx, double sy)
    {
        Point2D Scale(Point2D p)
            => new(center.X + (p.X - center.X) * sx, center.Y + (p.Y - center.Y) * sy);

        foreach (SubPath sp in SubPaths)
        {
            foreach (PathNode node in sp.Nodes)
            {
                node.Anchor = Scale(node.Anchor);
                node.InHandle = Scale(node.InHandle);
                node.OutHandle = Scale(node.OutHandle);
            }
        }

        TouchGeometry();
    }

    /// <summary>
    /// Maps every anchor and handle through an affine transform, in this path's own
    /// coordinate space.
    ///
    /// An affine map carries a Bézier exactly when it carries its four control points, so
    /// mapping the anchors and the handles is the whole of it - curves stay curves and
    /// nothing is flattened. This is the general case of <see cref="TranslateGeometryBy"/>,
    /// <see cref="ScaleGeometryAbout"/> and <see cref="RotateGeometryAbout"/>: a rotation,
    /// a scale and a translate are all affines, and a **conjugated** one (a scale stated in
    /// the frame a group establishes, turned back into the path's own frame) is an affine
    /// that none of the three expresses on its own.
    /// </summary>
    public void TransformGeometry(AffineTransform transform)
    {
        foreach (SubPath sp in SubPaths)
        {
            foreach (PathNode node in sp.Nodes)
            {
                node.Anchor = transform.Transform(node.Anchor);
                node.InHandle = transform.Transform(node.InHandle);
                node.OutHandle = transform.Transform(node.OutHandle);
            }
        }

        TouchGeometry();
    }

    /// <inheritdoc/>
    /// <summary>
    /// The CMYK components the file painted this path's fill and stroke with, when it used
    /// DeviceCMYK, or null.
    ///
    /// The model stores colours as RGB, and that conversion is not reversible: the importer
    /// computes r = (1-c)(1-k), so inverting it picks the maximum-black decomposition
    /// rather than the one the file used, and a viewer renders CMYK through a
    /// colour-managed transform besides. Keeping the original components is the only way
    /// an exported page can paint with the same ink values it arrived with.
    /// </summary>
    public double[]? SourceFillCmyk { get; set; }

    /// <summary>See <see cref="SourceFillCmyk"/>.</summary>
    public double[]? SourceStrokeCmyk { get; set; }

    public override LayerItem Clone()
    {
        var copy = new PathItem
        {
            Name = Name,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            Fill = _fill,
            Stroke = Stroke,
            Opacity = _opacity,

            // The original ink values are part of what the item is; a copy that dropped
            // them would export a different colour from the one it was cloned from.
            SourceFillCmyk = SourceFillCmyk is null ? null : (double[])SourceFillCmyk.Clone(),
            SourceStrokeCmyk = SourceStrokeCmyk is null ? null : (double[])SourceStrokeCmyk.Clone(),
        };

        // The rest of the stroke stack. The initialiser above set the first stroke through the compatibility
        // property, so the remainder is copied here - a clone that kept only the bottom stroke would quietly
        // lose artwork the moment a path had two.
        for (int i = 1; i < _strokes.Count; i++)
        {
            copy.Strokes.Add(_strokes[i]);
        }

        copy.SubPaths.AddRange(SubPaths.Select(sp => sp.Clone()));

        // Shared, not cloned: the definition is immutable, and a copy that forgot it would turn a
        // duplicated star into an anonymous outline.
        copy.Shape = Shape;

        // And the same for the live path effect: it is the description the stroke's width profile was derived
        // from, so a copy that dropped it would keep the converted widths and lose the reason they are those.
        copy.PathEffect = PathEffect;
        return copy;
    }
}
