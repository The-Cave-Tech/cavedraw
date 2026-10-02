using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VCCad.App.Controls;
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
/// **Every part of it is the canvas's, geometry and paint alike.** The geometry is
/// <see cref="StrokeOutlineBuilder"/>'s and <see cref="PlacedArt.Resolve"/>'s, and a placed asset's paint is
/// <c>CanvasWorkspace.PaintItemStandalone</c> - the canvas's own item painter, reached rather than copied, so a
/// gradient, an item's own stroke or the art a nested brush maps all preview as the canvas draws them. There is
/// deliberately nothing here that could form a second opinion about either.
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

    /// <summary>
    /// How the sample is fitted into the control: a uniform scale - never enlarging past 1:1, so a preview says
    /// "this is the brush at its own size" rather than "this is the brush made to fill a box" - and the translation
    /// that centres it. One answer, because <see cref="Render"/> draws with it and <see cref="ControlPoint"/>
    /// reports it.
    /// </summary>
    private (double Scale, double Left, double Top) Fit()
    {
        Rect box = Bounds;
        Rect2D world = BrushPreview.SamplePath.BoundingBox();

        if (box.Width <= 0 || box.Height <= 0 || (world.Width <= 0 && world.Height <= 0))
        {
            return (0, 0, 0);
        }

        double scale = Math.Min(1.0, Math.Min(
            (box.Width - 8) / Math.Max(world.Width, 1e-6),
            (box.Height - 8) / Math.Max(world.Height, 1e-6)));

        if (scale <= 0 || double.IsNaN(scale))
        {
            return (0, 0, 0);
        }

        return (
            scale,
            ((box.Width - (world.Width * scale)) / 2) - (world.Left * scale),
            ((box.Height - (world.Height * scale)) / 2) - (world.Top * scale));
    }

    /// <summary>
    /// Where a point of the sample's own model space lands in this control, so a caller can look at the pixel a
    /// placement names rather than at the whole picture. It is how a test asks "what did you draw here"; the
    /// preview itself uses <see cref="Fit"/> directly.
    /// </summary>
    internal Point ControlPoint(Point2D model)
    {
        (double scale, double left, double top) = Fit();
        return new Point((model.X * scale) + left, (model.Y * scale) + top);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        (double scale, double left, double top) = Fit();
        if (scale <= 0)
        {
            return;
        }

        var brush = new SolidColorBrush(Colors.Black);

        // The preview's own pass transform, which is this pass's `_paintWorld`: what a filtered asset rasterises
        // against. It is the same matrix the context is pushed with below.
        Avalonia.Matrix world = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top);

        using (context.PushTransform(world))
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
                    // The canvas's own item painter, reached rather than copied. The asset is drawn exactly as the
                    // canvas draws it - its fill and its gradient, its own strokes, a nested brush's art and a
                    // raster piece included - so the preview cannot come to a second opinion about the paint.
                    CanvasWorkspace.PaintItemStandalone(
                        context, _document, piece.Asset, piece.Opacity, ontoThePath, world);
                }
            }
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

    private static Matrix MatrixOf(AffineTransform transform)
        => new(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);
}
