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
    private StrokeSpec _stroke = StrokeSpec.Hairline(ColorRgb.Black);
    private double _opacity = 1.0;

    /// <summary>Subpaths in draw order; usually one, but compound shapes use many.</summary>
    public List<SubPath> SubPaths { get; } = new();

    /// <summary>The fill paint. See class remarks for the open/closed rule.</summary>
    public FillSpec Fill
    {
        get => _fill;
        set => SetField(ref _fill, value);
    }

    /// <summary>The stroke paint (width, colour, caps, joins).</summary>
    public StrokeSpec Stroke
    {
        get => _stroke;
        set => SetField(ref _stroke, value);
    }

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
        NotifyPropertyChanged(nameof(SubPaths));
        return sp;
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

        NotifyPropertyChanged(nameof(SubPaths));
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

        NotifyPropertyChanged(nameof(SubPaths));
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
        NotifyPropertyChanged(nameof(SubPaths));
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

        NotifyPropertyChanged(nameof(SubPaths));
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

        NotifyPropertyChanged(nameof(SubPaths));
    }

    /// <inheritdoc/>
    public override LayerItem Clone()
    {
        var copy = new PathItem
        {
            Name = Name,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            Fill = _fill,
            Stroke = _stroke,
            Opacity = _opacity,
        };
        copy.SubPaths.AddRange(SubPaths.Select(sp => sp.Clone()));
        return copy;
    }
}
