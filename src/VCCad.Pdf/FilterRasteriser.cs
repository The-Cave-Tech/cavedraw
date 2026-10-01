using System.Globalization;
using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// Turns a filtered object into the pixels its filter graph runs over, and then into the picture the exporter
/// places.
///
/// **A filter is a raster operation.** There is no PDF operator for a blur, a composite's arithmetic or a blend -
/// the section 9 gap list names soft masks and blend modes as separate, unmodelled things - so the honest way to
/// carry one is the way the canvas does: draw the object into a region, run the graph over it, and place the
/// answer. Here that answer is an image XObject, which is exactly how a PDF carries a picture.
///
/// The region is the filter's own (<c>x</c>, <c>y</c>, <c>width</c>, <c>height</c>, in the units
/// <c>ObjectBoundingBox</c> says), computed from the shape's **world** box, and the pixels are laid out over
/// exactly the rectangle the placement names - so nothing is rescaled twice and a filter is measured in model
/// units however large the page is. That is the same choice <c>FilterRenderer</c> makes for the canvas, which is
/// what keeps the drawing and the file agreeing about where the effect lands.
///
/// **What this refuses, and says so.** A graph that reads the picture behind the object cannot be evaluated
/// here: the exporter walks items one at a time and has no backdrop raster, so the input would be transparent
/// and the page would show a filter computed against nothing. A region larger than anything worth allocating is
/// refused too, for the same reason the canvas refuses it. Both are reported rather than exported unfiltered in
/// silence.
/// </summary>
internal static class FilterRasteriser
{
    /// <summary>One filtered object: the picture, and the world rectangle it belongs in.</summary>
    internal readonly record struct FilteredPicture(FilterBuffer Pixels, Rect2D Destination);

    /// <summary>The most pixels one filtered object may allocate along either axis.</summary>
    private const int MaximumPixels = 4096;

    /// <summary>Pixels per model unit the region is drawn at when nothing forces it smaller.</summary>
    private const double PreferredDensity = 2.0;

    /// <summary>
    /// Rasterises <paramref name="path"/> and runs <paramref name="filter"/> over it, or returns null and adds
    /// the reason to <paramref name="notes"/>.
    /// </summary>
    /// <param name="objectBounds">The shape's box in world units, which is what an <c>objectBoundingBox</c>
    /// region and an <c>objectBoundingBox</c> primitive length are fractions of.</param>
    /// <param name="toWorld">The transform from the path's own coordinates to world units - the artboard origin
    /// when the page is not sitting at the document's.</param>
    public static FilteredPicture? Rasterise(
        PathItem path,
        FilterSpec filter,
        Rect2D objectBounds,
        AffineTransform toWorld,
        double opacity,
        List<string> notes)
    {
        if (Unsupported(filter) is { } unsupported)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: it reads {unsupported}, and the export has no raster " +
                "of that to hand it - the object is exported unfiltered rather than through a filter evaluated " +
                "against nothing.");
            return null;
        }

        if (objectBounds.Width <= 0 || objectBounds.Height <= 0)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: the shape it is on has no area, so its region has " +
                "nothing to be a fraction of.");
            return null;
        }

        Rect2D region = RegionOf(filter, objectBounds);
        if (region.Width <= 0 || region.Height <= 0)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: its region is {region.Width} by {region.Height} " +
                "model units, so nothing would be evaluated.");
            return null;
        }

        // The region at one pixel per unit is the *placement* - where the result belongs - whatever density the
        // evaluation then happens at. The engine is told the same density, or a blur radius would be turned into
        // a pixel count by one number and measured by another.
        (int regionX, int regionY, int width, int height) = FilterEngine.RegionPixels(filter, objectBounds, 1.0);
        double scale = filter.HasFilterResolution ? 1.0 : Density(region);
        int pixelsWide = filter.HasFilterResolution ? filter.FilterResolutionX!.Value : Math.Max(1, (int)Math.Round(width * scale));
        int pixelsHigh = filter.HasFilterResolution ? filter.FilterResolutionY!.Value : Math.Max(1, (int)Math.Round(height * scale));

        if (pixelsWide > MaximumPixels || pixelsHigh > MaximumPixels)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: its region is {pixelsWide} by {pixelsHigh} pixels " +
                $"at the resolution the export would draw it, past the {MaximumPixels} this build will allocate " +
                "- the object is exported unfiltered rather than as a blur nobody asked to pay for.");
            return null;
        }

        FilterEngine engine;
        try
        {
            engine = new FilterEngine(filter, scale);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            notes.Add($"the filter '{filter.Name}' is not written: {exception.Message}.");
            return null;
        }

        // The pixels cover whole pixels from the rounded-outward region, so the picture's rectangle is what was
        // actually rasterised rather than the region's own corner - which is inside a pixel of it, and enough to
        // shift a shadow by a pixel on every export.
        double originX = regionX / scale;
        double originY = regionY / scale;
        var covered = new Rect2D(originX, originY, pixelsWide / scale, pixelsHigh / scale);

        var buffer = new FilterBuffer(pixelsWide, pixelsHigh);
        RasteriseInto(buffer, path, toWorld, originX, originY, scale, path.Opacity * opacity);

        FilterBuffer filtered;
        try
        {
            filtered = engine.EvaluateInPlace(buffer, null, objectBounds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            notes.Add($"the filter '{filter.Name}' is not written: {exception.Message}.");
            return null;
        }

        if (engine.UnsuppliedSourceInputs.Count > 0)
        {
            notes.Add(
                $"the filter '{filter.Name}' reads {string.Join(", ", engine.UnsuppliedSourceInputs)}, which the " +
                "export could not supply; those inputs were evaluated as transparent.");
        }

        return new FilteredPicture(filtered, covered);
    }

    /// <summary>
    /// The shape's own pixels over its filter's region, with no filter applied.
    ///
    /// This is exactly what the graph reads as <c>SourceGraphic</c> - and it is separated out so the two halves
    /// of "the export carries the filter" can be seen apart: whether the shape rasterised at all, and whether the
    /// graph changed it. A test that only looked at the exported bytes could not tell those apart, and a filter
    /// that quietly produced a blank picture would pass it.
    /// </summary>
    public static FilterBuffer? SourceGraphic(PathItem path, FilterSpec filter, Rect2D objectBounds)
    {
        Rect2D region = RegionOf(filter, objectBounds);
        if (region.Width <= 0 || region.Height <= 0)
        {
            return null;
        }

        (int regionX, int regionY, int width, int height) = FilterEngine.RegionPixels(filter, objectBounds, 1.0);
        double scale = Density(region);
        var buffer = new FilterBuffer(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));

        RasteriseInto(buffer, path, AffineTransform.Identity, regionX / scale, regionY / scale, scale, path.Opacity);
        return buffer;
    }

    /// <summary>
    /// The renderer-supplied input this graph reads that cannot be produced here, or null.
    ///
    /// Only <c>BackgroundImage</c>: the fill and the stroke are rasterised from the path itself, and
    /// <c>SourceGraphic</c>/<c>SourceAlpha</c> are that picture. Everything behind the object is not - the
    /// exporter draws items one at a time and has no backdrop to give.
    /// </summary>
    private static string? Unsupported(FilterSpec filter)
    {
        foreach (string name in filter.SourceInputsRead)
        {
            if (name == "BackgroundImage")
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The region the filter is evaluated over, in world units.
    ///
    /// The same arithmetic as <see cref="FilterEngine"/>'s own, written out because the engine keeps it private:
    /// the placement and the evaluation have to agree about the rectangle to the last pixel, or the picture lands
    /// beside the shape it belongs to.
    /// </summary>
    internal static Rect2D RegionOf(FilterSpec filter, Rect2D bounds)
        => filter.ObjectBoundingBox
            ? new Rect2D(
                bounds.X + (filter.X * bounds.Width),
                bounds.Y + (filter.Y * bounds.Height),
                filter.Width * bounds.Width,
                filter.Height * bounds.Height)
            : new Rect2D(filter.X, filter.Y, filter.Width, filter.Height);

    /// <summary>
    /// How many pixels to draw per model unit.
    ///
    /// A filter is measured in model units, so the density is what turns a blur radius into a number of pixels.
    /// Two per unit keeps a shadow's falloff smooth, and a page-sized region is drawn coarser rather than
    /// refused - the alternative is a filtered page that exports unfiltered because the page is large.
    /// </summary>
    private static double Density(Rect2D region)
    {
        double limited = Math.Min(
            MaximumPixels / Math.Max(region.Width, 1e-9),
            MaximumPixels / Math.Max(region.Height, 1e-9));

        return Math.Max(0.25, Math.Min(PreferredDensity, limited));
    }

    /// <summary>
    /// Draws the path into a region-sized buffer: its fill, then its strokes bottom to top.
    ///
    /// The fill and the strokes get their **own** masks and are composited in turn, because they are painted in
    /// their own colours - one mask in one colour would paint a blue fill under a red stroke blue.
    /// </summary>
    private static void RasteriseInto(
        FilterBuffer buffer,
        PathItem path,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale,
        double alpha)
    {
        if (path.Fill.IsVisible && path.Fill.Gradient is null && path.Fill.Hatch is null)
        {
            List<VectorRasteriser.Polygon> filled = FillPolygons(path, toWorld, originX, originY, scale);
            if (filled.Count > 0)
            {
                var mask = new VectorRasteriser.Mask(buffer.Width, buffer.Height);
                VectorRasteriser.Fill(mask, filled, path.Fill.Rule);
                VectorRasteriser.Composite(buffer, mask, path.Fill.Color, alpha * path.Fill.Color.A);
            }
        }

        foreach (StrokeSpec stroke in path.Strokes)
        {
            if (!stroke.HasVisibleOutline)
            {
                continue;
            }

            // The stroke's own geometry, resolved the one way the canvas and the exporter both resolve it: a
            // constant width is stroked, and anything that varies along its length is the outline it fills.
            // Turning the second into a stroke here would draw a different picture from the export of the same
            // shape without a filter.
            StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);
            var mask = new VectorRasteriser.Mask(buffer.Width, buffer.Height);

            if (plan.IsOutline)
            {
                var outlines = new List<VectorRasteriser.Polygon>();
                foreach (IReadOnlyList<Point2D> loop in plan.Outlines)
                {
                    if (loop.Count >= 2)
                    {
                        outlines.Add(new VectorRasteriser.Polygon(
                            ToRegion(loop, toWorld, originX, originY, scale), closed: true));
                    }
                }

                if (outlines.Count == 0)
                {
                    continue;
                }

                VectorRasteriser.Fill(mask, outlines, FillRule.NonZero);
            }
            else
            {
                List<VectorRasteriser.Polygon> pieces =
                    StrokePolygons(path, stroke, toWorld, originX, originY, scale);

                if (pieces.Count == 0)
                {
                    continue;
                }

                VectorRasteriser.Fill(mask, pieces, FillRule.NonZero);
            }

            VectorRasteriser.Composite(buffer, mask, stroke.Color, alpha * stroke.Color.A);
        }
    }

    /// <summary>The path's filled subpaths as region-space polygons.</summary>
    private static List<VectorRasteriser.Polygon> FillPolygons(
        PathItem path,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale)
    {
        var polygons = new List<VectorRasteriser.Polygon>();

        foreach (FlattenedOutline outline in PathFlattener.Flatten(path))
        {
            if (outline.Points.Count >= 2)
            {
                polygons.Add(new VectorRasteriser.Polygon(
                    ToRegion(outline.Points, toWorld, originX, originY, scale), closed: true));
            }
        }

        return polygons;
    }

    /// <summary>
    /// A stroked polyline as the polygons that cover it: a quad per segment, a wedge per join, and a cap at each
    /// end.
    ///
    /// Written out rather than taken from <see cref="StrokeOutlineBuilder.Outline"/> because the outline of a
    /// stroked path is an approximation of it - a miter is a point and a round join is an arc, and the offsetting
    /// that produces a single loop has to choose between them. This draws the picture from the same three numbers
    /// the file gives (width, cap, join).
    /// </summary>
    private static List<VectorRasteriser.Polygon> StrokePolygons(
        PathItem path,
        StrokeSpec stroke,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale)
    {
        var polygons = new List<VectorRasteriser.Polygon>();
        double half = Math.Max(0.0, stroke.Width) / 2;
        if (half <= 0)
        {
            return polygons;
        }

        foreach (FlattenedOutline outline in PathFlattener.FlattenForStroke(path))
        {
            IReadOnlyList<Point2D> points = outline.Points;
            int count = points.Count;
            if (count < 2)
            {
                continue;
            }

            Point2D[] region = ToRegion(points, toWorld, originX, originY, scale);
            int segments = outline.IsClosed ? count : count - 1;

            for (int i = 0; i < segments; i++)
            {
                Point2D a = region[i];
                Point2D b = region[(i + 1) % count];
                if (a.NearlyEquals(b, 1e-9))
                {
                    continue;
                }

                Vector2D along = Normalised(b - a) * half;
                var normal = new Vector2D(-along.Y, along.X);
                polygons.Add(new VectorRasteriser.Polygon(
                    new[] { a + normal, b + normal, b - normal, a - normal }, closed: true));
            }

            // Joins sit inside the shape a person drew, so every one of them is covered: leaving them out leaves
            // a wedge-shaped hole at every corner, which is exactly where a thick stroke shows.
            for (int i = 0; i < count; i++)
            {
                bool interior = outline.IsClosed || (i > 0 && i < count - 1);
                if (!interior)
                {
                    continue;
                }

                Point2D before = region[(i - 1 + count) % count];
                Point2D at = region[i];
                Point2D after = region[(i + 1) % count];

                if (at.NearlyEquals(before, 1e-9) || at.NearlyEquals(after, 1e-9))
                {
                    continue;
                }

                if (stroke.Join == StrokeJoin.Round)
                {
                    polygons.Add(Disc(at, half));
                    continue;
                }

                Vector2D inA = Normalised(at - before);
                Vector2D inB = Normalised(after - at);
                Vector2D outerA = new Vector2D(-inA.Y, inA.X) * half;
                Vector2D outerB = new Vector2D(-inB.Y, inB.X) * half;

                if (stroke.Join == StrokeJoin.Miter && MiterTip(at, outerA, outerB, half) is { } tip &&
                    (tip - at).Length <= Math.Max(1.0, stroke.MiterLimit) * half)
                {
                    polygons.Add(new VectorRasteriser.Polygon(
                        new[] { at, at + outerA, tip, at + outerB }, closed: true));
                    continue;
                }

                // Bevel, which is also what a miter past its limit becomes.
                polygons.Add(new VectorRasteriser.Polygon(
                    new[] { at, at + outerA, at + outerB }, closed: true));
            }

            if (!outline.IsClosed && stroke.Cap != StrokeCap.Butt)
            {
                AppendCaps(polygons, region, count, stroke.Cap, half);
            }
        }

        return polygons;
    }

    /// <summary>Where the two outer edges of a join cross, or null when they are parallel.</summary>
    private static Point2D? MiterTip(Point2D at, Vector2D outerA, Vector2D outerB, double half)
    {
        // Both edges pass through at+outerA and at+outerB and run along the segment they belong to, so the tip
        // is where those two rays meet. Solving in the edges' own directions keeps it exact for a right angle,
        // where an offset-based formula has to be special-cased.
        double cross = (outerA.X * outerB.Y) - (outerA.Y * outerB.X);
        if (Math.Abs(cross) < 1e-12)
        {
            return null;
        }

        // The rays meet at the origin of the normals' sum scaled by the miter length: for a join of half-width h
        // the tip is h / sin(theta/2) from the vertex along the bisector.
        Vector2D bisector = Normalised(outerA + outerB);
        if (bisector.Length <= 1e-12)
        {
            return null;
        }

        double sin = Math.Sqrt(Math.Max(0.0, (1 - (outerA.X * outerB.X + outerA.Y * outerB.Y) / (half * half)) / 2));
        if (sin <= 1e-9)
        {
            return null;
        }

        return at + (bisector * (half / sin));
    }

    private static void AppendCaps(
        List<VectorRasteriser.Polygon> polygons,
        Point2D[] region,
        int count,
        StrokeCap cap,
        double half)
    {
        if (cap == StrokeCap.Round)
        {
            polygons.Add(Disc(region[0], half));
            polygons.Add(Disc(region[count - 1], half));
            return;
        }

        foreach (Point2D end in new[] { region[0], region[count - 1] })
        {
            Point2D inward = end.NearlyEquals(region[0], 1e-9) ? region[1] : region[count - 2];
            if (end.NearlyEquals(inward, 1e-9))
            {
                continue;
            }

            Vector2D along = Normalised(end - inward) * half;
            var normal = new Vector2D(-along.Y, along.X);
            polygons.Add(new VectorRasteriser.Polygon(
                new[] { end + normal, end + along + normal, end + along - normal, end - normal }, closed: true));
        }
    }

    /// <summary>A circle as a polygon. Twelve sides is under a hundredth of a unit of error at any width a person
    /// draws, and a filter that blurs it cannot see the difference.</summary>
    private static VectorRasteriser.Polygon Disc(Point2D centre, double radius)
    {
        const int Sides = 12;
        var points = new Point2D[Sides];

        for (int i = 0; i < Sides; i++)
        {
            double angle = 2 * Math.PI * i / Sides;
            points[i] = new Point2D(
                centre.X + (Math.Cos(angle) * radius),
                centre.Y + (Math.Sin(angle) * radius));
        }

        return new VectorRasteriser.Polygon(points, closed: true);
    }

    private static Vector2D Normalised(Vector2D value)
    {
        double length = value.Length;
        return length <= 1e-12 ? new Vector2D(0, 0) : new Vector2D(value.X / length, value.Y / length);
    }

    /// <summary>Path-local points to raster pixels: through the world transform, onto the region's grid.</summary>
    private static Point2D[] ToRegion(
        IReadOnlyList<Point2D> points,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale)
    {
        var result = new Point2D[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            Point2D world = toWorld.Transform(points[i]);
            result[i] = new Point2D((world.X - originX) * scale, (world.Y - originY) * scale);
        }

        return result;
    }

    /// <summary>
    /// The <c>cm</c> that puts the region's image where it belongs, as the four basis numbers.
    ///
    /// A PDF image is painted into the unit square with its first row along the bottom, while these pixels start
    /// at the region's **top** left - so the vertical basis is negated, the same flip <c>PaintImage</c> folds in
    /// for an imported image.
    /// </summary>
    public static string Placement(FilteredPicture picture, AffineTransform toDoc)
    {
        Rect2D region = picture.Destination;
        Point2D topLeft = toDoc.Transform(new Point2D(region.Left, region.Top));
        Point2D topRight = toDoc.Transform(new Point2D(region.Right, region.Top));
        Point2D bottomLeft = toDoc.Transform(new Point2D(region.Left, region.Bottom));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PdfDocumentExporter.Num(topRight.X - topLeft.X)} {PdfDocumentExporter.Num(topRight.Y - topLeft.Y)} " +
            $"{PdfDocumentExporter.Num(bottomLeft.X - topLeft.X)} {PdfDocumentExporter.Num(bottomLeft.Y - topLeft.Y)} " +
            $"{PdfDocumentExporter.Num(topLeft.X)} {PdfDocumentExporter.Num(topLeft.Y)}");
    }
}
