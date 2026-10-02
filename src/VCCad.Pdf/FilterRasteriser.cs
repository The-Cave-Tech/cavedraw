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
        if (Unsupported(filter, path) is { } unsupported)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: {unsupported} - the object is exported unfiltered " +
                "rather than through a filter evaluated against a picture the file did not ask for.");
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

        return Evaluate(path, new[] { filter }, objectBounds, toWorld, opacity, notes);
    }

    /// <summary>
    /// The path drawn with the raster effects its first effect-carrying stroke holds, or null when it has none.
    ///
    /// **This is the filter machinery reached from the stroke side, not a second renderer.** A blur, a shadow and a
    /// glow have no PDF operator and are not geometry, so the only honest way to carry one is the way a filter is
    /// carried: draw the path into pixels over a region and place the answer as an image. What differs is only where
    /// the graph comes from - <see cref="RasterEffectFilters.ToFilter"/> builds it from the stroke's effect list
    /// rather than the document reading it out of the file - and the region, which starts from the stroke's own
    /// extent because a stroke reaches beyond its centreline and an outer glow further still.
    ///
    /// The effects are applied **in order**, each taking the previous one's answer as its source, which is what makes
    /// the model's order the picture. The region is the first effect's, exactly as <c>FilterRenderer</c> decides it
    /// for the canvas: one chain has one canvas, or the second effect would be evaluated against a rectangle the
    /// first one never produced.
    ///
    /// A path whose strokes carry **different** effects renders the first such stroke's, which is the documented
    /// limit of drawing a path as one picture (issue #104's stack would give each stroke its own).
    /// </summary>
    public static FilteredPicture? RasteriseStrokeEffects(
        PathItem path,
        AffineTransform toWorld,
        double opacity,
        List<string> notes)
    {
        if (EffectStroke(path) is not { } stroke)
        {
            return null;
        }

        // A gradient or a hatch has no single colour for this rasteriser to composite, so the path is not drawn
        // here at all - and a picture that dropped the fill would be a worse answer than the vectors below, which
        // draw it correctly and lose only the effect.
        if (UnsupportedPaint(path) is { } unsupportedPaint)
        {
            notes.Add(
                $"the stroke's raster effect is not written: {unsupportedPaint} - the path is exported as vectors " +
                "without the effect rather than through a picture the file did not ask for.");
            return null;
        }

        // The stroke's own extent, not the centreline box: half the width reaches beyond the line, and an outer
        // glow reaches further still. The canvas builds its region the same way (`RasterFiltersFor`).
        Rect2D bounds = path.WorldBounds();
        Rect2D strokeBounds = bounds.Inflated(Math.Max(0.0, stroke.Width) / 2);

        if (strokeBounds.Width <= 0 || strokeBounds.Height <= 0)
        {
            notes.Add(
                $"the stroke's raster effect is not written: the shape it is on has no area, so its region is " +
                $"{strokeBounds.Width} by {strokeBounds.Height} model units.");
            return null;
        }

        var filters = new List<FilterSpec>();
        foreach (RasterEffectSpec effect in stroke.AllRasterEffects)
        {
            if (RasterEffectFilters.ToFilter(effect, stroke.Color, strokeBounds) is { } filter)
            {
                filters.Add(filter);
            }
        }

        if (filters.Count == 0)
        {
            return null;
        }

        return Evaluate(path, filters, bounds, toWorld, opacity, notes);
    }

    /// <summary>The first stroke that carries raster effects, or null - the one the whole path is drawn with.</summary>
    private static StrokeSpec? EffectStroke(PathItem path)
    {
        foreach (StrokeSpec stroke in path.Strokes)
        {
            if (stroke.AllRasterEffects is { Count: > 0 })
            {
                return stroke;
            }
        }

        return null;
    }

    /// <summary>
    /// The shared tail of both entry points: one region, the path drawn into it, and every graph in the chain
    /// evaluated over the previous one's answer.
    ///
    /// The **first** graph decides the region and the pixel grid, because the chain produces one picture and one
    /// placement - and that is the choice <c>FilterRenderer</c> makes for the canvas, so the file and the drawing
    /// cannot disagree about where the effect lands.
    /// </summary>
    private static FilteredPicture? Evaluate(
        PathItem path,
        IReadOnlyList<FilterSpec> filters,
        Rect2D objectBounds,
        AffineTransform toWorld,
        double opacity,
        List<string> notes)
    {
        FilterSpec filter = filters[0];

        // The region at the **evaluation density** is both the buffer's pixel size and, divided by that density,
        // where its first pixel sits in model units - and the engine is given the same density, or a blur radius
        // would be turned into a pixel count by one number and measured by another. Asking at one pixel per unit
        // and then dividing by the density is what put the drawing window half a region away on a page that is not
        // at the document origin: the artwork is drawn at the artboard's offset, so a buffer whose origin is
        // displaced from the region's model rectangle leaves the shape outside its own bitmap and the page draws
        // nothing at all.
        double density = filter.HasFilterResolution ? 1.0 : Density(RegionOf(filter, objectBounds));
        (int regionX, int regionY, int width, int height) =
            FilterEngine.RegionPixels(filter, objectBounds, density);
        int pixelsWide = filter.HasFilterResolution ? filter.FilterResolutionX!.Value : width;
        int pixelsHigh = filter.HasFilterResolution ? filter.FilterResolutionY!.Value : height;

        if (pixelsWide > MaximumPixels || pixelsHigh > MaximumPixels)
        {
            notes.Add(
                $"the filter '{filter.Name}' is not written: its region is {pixelsWide} by {pixelsHigh} pixels " +
                $"at the resolution the export would draw it, past the {MaximumPixels} this build will allocate " +
                "- the object is exported unfiltered rather than as a blur nobody asked to pay for.");
            return null;
        }

        var engines = new List<FilterEngine>(filters.Count);
        foreach (FilterSpec step in filters)
        {
            try
            {
                engines.Add(new FilterEngine(step, density));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                notes.Add($"the filter '{step.Name}' is not written: {exception.Message}.");
                return null;
            }
        }

        // The pixels cover whole pixels from the rounded-outward region, so the picture's rectangle is what was
        // actually rasterised rather than the region's own corner - which is inside a pixel of it, and enough to
        // shift a shadow by a pixel on every export. A named `filterRes` samples the same model rectangle more
        // finely, so there the placement stays the region itself.
        double originX = regionX / density;
        double originY = regionY / density;
        Rect2D covered = filter.HasFilterResolution
            ? RegionOf(filter, objectBounds)
            : new Rect2D(originX, originY, width / density, height / density);

        var buffer = new FilterBuffer(pixelsWide, pixelsHigh);
        double alpha = path.Opacity * opacity;
        RasteriseInto(buffer, path, toWorld, originX, originY, density, alpha);

        FilterSources? sources = SourcePictures(
            path, filters, toWorld, originX, originY, density, pixelsWide, pixelsHigh, alpha);

        FilterBuffer filtered = buffer;
        for (int i = 0; i < filters.Count; i++)
        {
            FilterEngine engine = engines[i];
            try
            {
                filtered = engine.EvaluateInPlace(filtered, sources, objectBounds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                notes.Add($"the filter '{filters[i].Name}' is not written: {exception.Message}.");
                return null;
            }

            // Every renderer-supplied input this build can produce has been handed over, so anything the engine had
            // to leave out is a genuine gap rather than an omission here - and saying which one it was is the
            // difference between a reported approximation and a picture nobody can explain.
            if (engine.UnsuppliedSourceInputs.Count > 0)
            {
                notes.Add(
                    $"the filter '{filters[i].Name}' reads {string.Join(", ", engine.UnsuppliedSourceInputs)}, " +
                    "which the export could not supply; those inputs were evaluated as transparent.");
            }
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

        (int regionX, int regionY, int width, int height) =
            FilterEngine.RegionPixels(filter, objectBounds, Density(region));
        double scale = Density(region);
        var buffer = new FilterBuffer(Math.Max(1, width), Math.Max(1, height));

        RasteriseInto(buffer, path, AffineTransform.Identity, regionX / scale, regionY / scale, scale, path.Opacity);
        return buffer;
    }

    /// <summary>
    /// What about this object the rasteriser cannot draw faithfully, as a phrase for the export note, or null.
    ///
    /// Two things. The picture **behind** the object, because the exporter draws one item at a time and has no
    /// backdrop raster to hand over. And a fill that is not one colour: a gradient is written as a shading and a
    /// hatch as clipped line art, and neither has a single colour to composite, so the shape's own pixels could
    /// not be produced and the graph would be evaluated against a picture the file did not ask for.
    ///
    /// <c>SourceGraphic</c> and <c>SourceAlpha</c> are always available, and so are <c>FillPaint</c> and
    /// <c>StrokePaint</c>, which are rasterised from the path itself (<see cref="SourcePictures"/>).
    /// </summary>
    private static string? Unsupported(FilterSpec filter, PathItem path)
    {
        foreach (string name in filter.SourceInputsRead)
        {
            if (name == "BackgroundImage")
            {
                return "it reads BackgroundImage, which is the picture behind the object, and the export draws one " +
                       "item at a time with no backdrop to hand it";
            }
        }

        return UnsupportedPaint(path);
    }

    /// <summary>
    /// The paint this rasteriser cannot draw, or null. A fill that is not one colour: a gradient is written as a
    /// shading and a hatch as clipped line art, and neither has a single colour to composite, so the shape's own
    /// pixels could not be produced.
    ///
    /// Shared by the filter and the stroke-effect entry points because both rasterise the same path
    /// (<see cref="RasteriseInto"/>) and would drop the same fill.
    /// </summary>
    private static string? UnsupportedPaint(PathItem path)
    {
        if (!path.Fill.IsVisible || (path.Fill.Gradient is null && path.Fill.Hatch is null))
        {
            return null;
        }

        return path.Fill.Gradient is not null
            ? "the shape is filled with a gradient, which this exporter writes as a shading rather than as a " +
              "colour it can rasterise"
            : "the shape is filled with a hatch, which this exporter writes as clipped line art rather than as " +
              "a colour it can rasterise";
    }

    /// <summary>
    /// The fill and stroke pictures a graph reads, or null when it reads neither.
    ///
    /// SVG names <c>FillPaint</c> and <c>StrokePaint</c> as the shape drawn in its own fill and its own stroke.
    /// They are facts only a renderer holds: the pixels of a filled and stroked shape are the two painted
    /// together, so there is no way back from them to either one, which is why the engine takes them from the
    /// caller rather than deriving them. The canvas hands both over for exactly this reason, so a graph that reads
    /// one has to be given the same picture here or the file and the drawing disagree where the effect is.
    ///
    /// A shape with no fill has a **transparent** FillPaint rather than a missing one - SVG's `none` is a paint
    /// like any other - and the engine is told so, which is why an empty picture is supplied rather than left out.
    /// </summary>
    private static FilterSources? SourcePictures(
        PathItem path,
        IReadOnlyList<FilterSpec> filters,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale,
        int width,
        int height,
        double alpha)
    {
        bool wantsFill = false;
        bool wantsStroke = false;

        foreach (FilterSpec filter in filters)
        {
            foreach (string name in filter.SourceInputsRead)
            {
                wantsFill |= name == "FillPaint";
                wantsStroke |= name == "StrokePaint";
            }
        }

        if (!wantsFill && !wantsStroke)
        {
            return null;
        }

        return new FilterSources
        {
            FillPaint = wantsFill
                ? Paint(path, toWorld, originX, originY, scale, width, height, alpha, fill: true, strokes: false)
                : null,
            StrokePaint = wantsStroke
                ? Paint(path, toWorld, originX, originY, scale, width, height, alpha, fill: false, strokes: true)
                : null,
        };
    }

    /// <summary>One half of the shape's paint, over the region.</summary>
    private static FilterBuffer Paint(
        PathItem path,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale,
        int width,
        int height,
        double alpha,
        bool fill,
        bool strokes)
    {
        var buffer = new FilterBuffer(Math.Max(1, width), Math.Max(1, height));
        RasteriseInto(buffer, path, toWorld, originX, originY, scale, alpha, fill, strokes);
        return buffer;
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
    ///
    /// Either half can be asked for alone, which is what SVG's `FillPaint` and `StrokePaint` are: the shape drawn
    /// in its own fill, and the shape drawn in its own stroke. A renderer is the only thing that can produce them
    /// - the pixels of the two painted together do not separate back into them - and the canvas hands both over
    /// for the same reason, so a graph reading one has to be given the same picture here or the export and the
    /// canvas disagree exactly where the effect is.
    /// </summary>
    private static void RasteriseInto(
        FilterBuffer buffer,
        PathItem path,
        AffineTransform toWorld,
        double originX,
        double originY,
        double scale,
        double alpha,
        bool fill = true,
        bool strokes = true)
    {
        if (fill && path.Fill.IsVisible && path.Fill.Gradient is null && path.Fill.Hatch is null)
        {
            List<VectorRasteriser.Polygon> filled = FillPolygons(path, toWorld, originX, originY, scale);
            if (filled.Count > 0)
            {
                var mask = new VectorRasteriser.Mask(buffer.Width, buffer.Height);
                VectorRasteriser.Fill(mask, filled, path.Fill.Rule);
                VectorRasteriser.Composite(buffer, mask, path.Fill.Color, alpha * path.Fill.Color.A);
            }
        }

        if (!strokes)
        {
            return;
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
