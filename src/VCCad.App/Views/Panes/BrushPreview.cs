using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Geometry;
using MediaGeometry = Avalonia.Media.Geometry;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The sample path the brush editor previews on, and the two answers a preview needs about it.
///
/// **The preview computes no geometry of its own.** A nib's picture is
/// <see cref="StrokeOutlineBuilder.Outline"/> and an art, pattern or scatter brush's picture is
/// <see cref="PlacedArt.Resolve"/> - the same two seams `CanvasWorkspace` draws a real stroke through, and the same
/// two the PDF writer exports through. A preview built by a second renderer is a preview that lies, and the first
/// person to find out is the one who applies the brush and gets something else, so there is deliberately nothing
/// here that could form a second opinion: <see cref="Outline"/> *is* the builder call and <see cref="Art"/> *is* the
/// resolve call.
///
/// The sample is a bent open path rather than a straight one, because the two things a brush does that a straight
/// line cannot show are the ones a person is looking at when they choose: a nib's width follows the direction of
/// travel, and a pattern brush's corner detection needs a turn to recognise.
/// </summary>
internal static class BrushPreview
{
    /// <summary>The path every preview is drawn on. One path, so two previews are comparable.</summary>
    public static PathItem SamplePath { get; } = MakeSamplePath();

    /// <summary>
    /// The stroke a sample path is drawn with: the brush under the preview, at <paramref name="width"/>.
    ///
    /// The width travels with it because a brush **modulates** a stroke rather than replacing it - clearing a brush
    /// leaves the width behind - so a preview with no width would show the brush doing something the model never
    /// says it does.
    /// </summary>
    public static StrokeSpec SampleStroke(double width, BrushSpec? brush)
        => new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Round, StrokeJoin.Round, 4.0) with { Brush = brush };

    /// <summary>
    /// The outlines that cover the sample path - the pipeline's own answer, not this file's.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(PathItem sample, BrushSpec? brush, double width)
        => StrokeOutlineBuilder.Outline(sample, SampleStroke(width, brush));

    /// <summary>
    /// Where the brush's artwork lands along the sample path - <see cref="PlacedArt.Resolve"/>, the same call the
    /// canvas makes when it draws a brushed stroke for real.
    ///
    /// A nib places no artwork, and neither does a brush whose asset the document does not have: both answer with
    /// nothing, which is what the canvas gets too.
    /// </summary>
    public static IReadOnlyList<PlacedArt> Art(CadDocument? document, PathItem sample, BrushSpec? brush)
        => document is null || brush is null
            ? Array.Empty<PlacedArt>()
            : PlacedArt.Resolve(document, sample, brush);

    private static PathItem MakeSamplePath()
    {
        var path = new PathItem { Name = "preview", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(80, 80)));
        sub.Nodes.Add(new PathNode(new Point2D(120, 40)));
        return path;
    }
}

/// <summary>
/// The live preview of the brush being edited: the sample path drawn with the brush, in a box.
///
/// <see cref="Outline"/> and <see cref="Placed"/> are the pipeline's own answers, held here so a test can ask what
/// the preview is about to draw rather than only that a preview exists. <see cref="Render"/> fills exactly those
/// loops and paints exactly those placements, so the two cannot come apart.
///
/// **What is the canvas's and what is not.** The geometry is the canvas's: the loops are
/// <see cref="StrokeOutlineBuilder"/>'s and the placements are <see cref="PlacedArt.Resolve"/>'s, composed onto the
/// path the same way `CanvasWorkspace.PaintStrokeArt` composes them. The **paint** of a placed asset is not: the
/// canvas paints an item through its own private painter, which a pane cannot reach, so an asset is previewed as
/// its own shape filled in the stroke's colour. A vector tile therefore previews with the right outline and the
/// wrong fill; a person who wants the exact colours has the canvas, and the alternative - a second item painter
/// here - is the second renderer this whole type exists to avoid.
/// </summary>
public sealed class BrushPreviewView : Control
{
    private CadDocument? _document;
    private BrushSpec? _brush;
    private double _width = 6.0;

    /// <summary>The stroke width the sample is drawn at, which a brush modulates rather than replaces.</summary>
    public double StrokeWidth
    {
        get => _width;
        set
        {
            _width = value;
            Refresh();
        }
    }

    /// <summary>The outlines the preview fills, as the pipeline resolved them.</summary>
    internal IReadOnlyList<IReadOnlyList<Point2D>> Outline { get; private set; } = Array.Empty<IReadOnlyList<Point2D>>();

    /// <summary>The artwork the preview paints, as the pipeline resolved it.</summary>
    internal IReadOnlyList<PlacedArt> Placed { get; private set; } = Array.Empty<PlacedArt>();

    /// <summary>Points the preview at a brush. A null brush is the plain stroke, which is a state and not nothing.</summary>
    public void Show(CadDocument? document, BrushSpec? brush)
    {
        _document = document;
        _brush = brush;
        Refresh();
    }

    private void Refresh()
    {
        Outline = BrushPreview.Outline(BrushPreview.SamplePath, _brush, _width);
        Placed = BrushPreview.Art(_document, BrushPreview.SamplePath, _brush);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect box = Bounds;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        Rect2D world = BrushPreview.SamplePath.BoundingBox();
        if (world.Width <= 0 && world.Height <= 0)
        {
            return;
        }

        // Uniform, and never enlarging past 1:1, so a preview says "this is the brush at its own size" rather than
        // "this is the brush made to fill a box".
        double scale = Math.Min(1.0, Math.Min(
            (box.Width - 8) / Math.Max(world.Width, 1e-6),
            (box.Height - 8) / Math.Max(world.Height, 1e-6)));

        if (scale <= 0 || double.IsNaN(scale))
        {
            return;
        }

        double left = ((box.Width - (world.Width * scale)) / 2) - (world.Left * scale);
        double top = ((box.Height - (world.Height * scale)) / 2) - (world.Top * scale);

        var brush = new SolidColorBrush(Colors.Black);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top)))
        {
            if (Outline.Count > 0)
            {
                context.DrawGeometry(brush, null, GeometryOf(Outline));
            }

            foreach (PlacedArt piece in Placed)
            {
                // The push is the canvas's own composition: the item's painters draw at its artboard origin, so the
                // origin is taken back off before the placement carries the frame onto the path.
                Vector2D origin = piece.Asset.ArtboardOffset();
                AffineTransform ontoThePath = piece.Placement.Transform.Compose(
                    AffineTransform.CreateTranslation(-origin.X, -origin.Y));

                using (context.PushTransform(MatrixOf(ontoThePath)))
                {
                    PaintAsset(context, piece.Asset, brush);
                }
            }
        }
    }

    /// <summary>An asset's own shape, in the asset's own frame.</summary>
    private static void PaintAsset(DrawingContext context, LayerItem item, IBrush brush)
    {
        switch (item)
        {
            case PathItem path:
                context.DrawGeometry(brush, null, GeometryOf(path.SubPaths));
                break;

            case ArtGroup group:
                using (context.PushTransform(MatrixOf(group.Transform)))
                {
                    foreach (LayerItem child in group.Children)
                    {
                        PaintAsset(context, child, brush);
                    }
                }

                break;

            case ImageItem image:
                if (image.Placement.Width > 0 && image.Placement.Height > 0)
                {
                    context.DrawGeometry(brush, null, RectGeometry(image.Placement));
                }

                break;
        }
    }

    /// <summary>The loops the outline seam answered with, as drawable geometry. Nonzero, like the canvas's.</summary>
    private static MediaGeometry GeometryOf(IReadOnlyList<IReadOnlyList<Point2D>> loops)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(Avalonia.Media.FillRule.NonZero);
            foreach (IReadOnlyList<Point2D> loop in loops)
            {
                if (loop.Count < 2)
                {
                    continue;
                }

                g.BeginFigure(new Point(loop[0].X, loop[0].Y), true);
                for (int i = 1; i < loop.Count; i++)
                {
                    g.LineTo(new Point(loop[i].X, loop[i].Y));
                }

                g.EndFigure(true);
            }
        }

        return geometry;
    }

    /// <summary>A path's own curves, in its own coordinates - the same figure `ObjectThumbnail` builds, unfitted.</summary>
    private static MediaGeometry GeometryOf(IReadOnlyList<SubPath> subPaths)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(Avalonia.Media.FillRule.NonZero);
            foreach (SubPath sub in subPaths)
            {
                if (sub.Nodes.Count == 0)
                {
                    continue;
                }

                g.BeginFigure(new Point(sub.Nodes[0].Anchor.X, sub.Nodes[0].Anchor.Y), sub.IsClosed);

                int last = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    PathNode from = sub.Nodes[i];
                    PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                    g.CubicBezierTo(
                        new Point(from.OutHandle.X, from.OutHandle.Y),
                        new Point(to.InHandle.X, to.InHandle.Y),
                        new Point(to.Anchor.X, to.Anchor.Y));
                }

                g.EndFigure(sub.IsClosed);
            }
        }

        return geometry;
    }

    private static MediaGeometry RectGeometry(Rect2D rect)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left, rect.Top), true);
            g.LineTo(new Point(rect.Right, rect.Top));
            g.LineTo(new Point(rect.Right, rect.Bottom));
            g.LineTo(new Point(rect.Left, rect.Bottom));
            g.EndFigure(true);
        }

        return geometry;
    }

    private static Matrix MatrixOf(AffineTransform transform)
        => new(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);
}
