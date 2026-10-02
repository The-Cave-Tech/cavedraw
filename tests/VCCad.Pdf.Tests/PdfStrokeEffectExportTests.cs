using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A stroke's **raster effects** in the export path, asserted at known pixels and **differentially**.
///
/// The canvas half landed first (`feat(app): draw a stroke's raster effects on the canvas`), and a picture that is
/// drawn and not exported is the disagreement this repository names as the worst of the three outcomes. So these
/// tests read the pixels the exporter would place, taken from the same <see cref="FilterRasteriser"/> the exporter
/// calls, and they compare the same document **with and without** the effect rather than against a nominal value -
/// a threshold on an absolute number has proved nothing here before (the paper renders at 224, so "brighter than
/// N" was also true of a blank page).
///
/// The four effects are covered where they differ from doing nothing:
/// <list type="bullet">
/// <item>a blur softens the stroke's own body and spreads it past its edge;</item>
/// <item>a drop shadow puts ink where the stroke is not, displaced by its offsets and in its own colour;</item>
/// <item>an outer glow puts ink outside the stroke;</item>
/// <item>an inner glow does not, and puts it inside instead.</item>
/// </list>
///
/// The export-level half is written against <see cref="PdfExportSupport"/> the way the filter's tests are: with the
/// declaration still false it pins the gap, and the day the exporter writes the effect it asserts the picture
/// instead - so this file cannot go stale without failing.
/// </summary>
public class PdfStrokeEffectExportTests
{
    /// <summary>
    /// Whether the export declares a stroke's raster effect written - **the one thing that flips between the gap
    /// and its closing**, read from the declaration rather than written down twice.
    /// </summary>
    private static bool Flipped => PdfExportSupport.Find("rasterEffect")!.Written;

    /// <summary>
    /// A closed rectangle stroked with a 10-unit red line and **no fill**, so every pixel the picture has is the
    /// stroke's own and a point inside the rectangle is genuinely empty.
    ///
    /// The band is therefore the outline of (60,60)-(160,110) inflated by half the width: on the top edge it covers
    /// y 55..65, and its outer edge is y=55.
    /// </summary>
    private static CadDocument Document(params RasterEffectSpec[] effects)
        => Document(new ColorRgb(1, 0, 0), effects);

    /// <summary>The same, in a named stroke colour - a semi-transparent one is how an inner glow is made visible,
    /// because it is drawn *under* the artwork and an opaque line hides it.</summary>
    private static CadDocument Document(ColorRgb strokeColour, params RasterEffectSpec[] effects)
        => DocumentAt(0, 0, strokeColour, effects);

    /// <summary>
    /// The rectangle's corner moved by <paramref name="dx"/>, <paramref name="dy"/> - the same shape on a page that
    /// is **not** at the document origin, which is where a region and the artwork it belongs to can disagree.
    /// </summary>
    private static CadDocument DocumentAt(double dx, double dy, ColorRgb strokeColour, params RasterEffectSpec[] effects)
    {
        CadDocument document = CadDocument.CreateDefault("Stroke raster effects");

        var path = new PathItem { Name = "frame", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(60 + dx, 60 + dy)));
        sub.Nodes.Add(new PathNode(new Point2D(160 + dx, 60 + dy)));
        sub.Nodes.Add(new PathNode(new Point2D(160 + dx, 110 + dy)));
        sub.Nodes.Add(new PathNode(new Point2D(60 + dx, 110 + dy)));

        var stroke = new StrokeSpec(true, strokeColour, 10, StrokeCap.Butt, StrokeJoin.Miter, 4);
        if (effects.Length > 0)
        {
            stroke = stroke with { RasterEffects = new RasterEffectStack(effects) };
        }

        path.Strokes.Clear();
        path.Strokes.Add(stroke);
        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    private static PathItem Shape(CadDocument document) => document.AllPaths().Single();

    /// <summary>The stroke region the effect's graph is built from, exactly as the rasteriser builds it.</summary>
    private static Rect2D StrokeBounds(PathItem path)
        => path.WorldBounds().Inflated(path.Strokes[0].Width / 2);

    /// <summary>
    /// The same picture with **no effect applied** - what the graph reads as <c>SourceGraphic</c>.
    ///
    /// Taken through <see cref="FilterRasteriser.SourceGraphic"/> rather than by exporting twice, because the two
    /// pictures have to sit on the *same* pixel grid for a per-pixel comparison to mean anything, and this is the
    /// one entry point that rasterises over a named filter's region without running its graph.
    /// </summary>
    private static (FilterBuffer Pixels, Rect2D Region) Plain(PathItem path, RasterEffectSpec effect)
    {
        FilterSpec filter = RasterEffectFilters.ToFilter(effect, path.Strokes[0].Color, StrokeBounds(path))!;
        Rect2D bounds = path.WorldBounds();
        return (FilterRasteriser.SourceGraphic(path, filter, bounds)!, FilterRasteriser.RegionOf(filter, bounds));
    }

    /// <summary>The path drawn with its stroke's effects, which is what the exporter would place.</summary>
    private static FilterRasteriser.FilteredPicture Rendered(PathItem path, List<string>? notes = null)
        => FilterRasteriser.RasteriseStrokeEffects(
            path, AffineTransform.Identity, 1.0, notes ?? new List<string>())!.Value;

    /// <summary>A world point as a pixel of a filtered picture's own grid.</summary>
    private static (int X, int Y) Pixel(FilterRasteriser.FilteredPicture picture, Point2D world)
        => Pixel(picture.Pixels, picture.Destination, world);

    /// <summary>A world point as a pixel of a buffer that covers a named region.</summary>
    private static (int X, int Y) Pixel(FilterBuffer buffer, Rect2D region, Point2D world)
    {
        double scale = buffer.Width / region.Width;
        return (
            (int)Math.Round((world.X - region.X) * scale),
            (int)Math.Round((world.Y - region.Y) * scale));
    }

    /// <summary>How many pixels carry any ink at all - the spread the effect reaches over.</summary>
    private static int Spread(FilterBuffer buffer)
    {
        int count = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                if (buffer.AlphaAt(x, y) > 0.01f)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ------------------------------------------------------------------ the four effects

    /// <summary>
    /// **A blur softens the stroke.** Differentially: the stroke's own body falls from opaque to a partial value,
    /// and a point three units outside the line - empty before - now carries ink.
    /// </summary>
    [Fact]
    public void ABlurSoftensTheStrokeAndSpreadsItPastItsEdge()
    {
        RasterEffectSpec effect = RasterEffectSpec.Blur(6);
        (FilterBuffer plainPixels, Rect2D region) = Plain(Shape(Document()), effect);
        FilterRasteriser.FilteredPicture blurred = Rendered(Shape(Document(effect)));

        // (110,60) is the centre of the top edge's band, which runs 55..65.
        (int cx, int cy) = Pixel(blurred, new Point2D(110, 60));

        // (110,52) is three units clear of the band's outer edge at y=55.
        (int ox, int oy) = Pixel(blurred, new Point2D(110, 52));

        (int px, int py) = Pixel(plainPixels, region, new Point2D(110, 60));
        (int qx, int qy) = Pixel(plainPixels, region, new Point2D(110, 52));

        Assert.True(plainPixels.AlphaAt(px, py) > 0.95f, $"the plain stroke is opaque: {plainPixels.AlphaAt(px, py)}");
        Assert.Equal(0f, plainPixels.AlphaAt(qx, qy));

        float body = blurred.Pixels.AlphaAt(cx, cy);
        float outside = blurred.Pixels.AlphaAt(ox, oy);

        Assert.True(body < 0.9f, $"the blurred stroke's own body is softened, was {body} at {cx},{cy}");
        Assert.True(body > 0.1f, $"the blurred stroke's body is still there, was {body}");
        Assert.True(outside > 0.05f, $"the blur spread past the edge, was {outside} at {ox},{oy}");

        // And it covers *more* pixels than the plain stroke, which is what "a radius, not a fixed bleed" means.
        Assert.True(Spread(blurred.Pixels) > Spread(plainPixels), "the blur covers more pixels than the plain stroke");
    }

    /// <summary>
    /// **A drop shadow puts ink where the stroke is not**, displaced by its offsets and drawn in the stroke's own
    /// colour when it carries no tint of its own.
    /// </summary>
    [Fact]
    public void ADropShadowPutsInkWhereTheStrokeIsNot()
    {
        RasterEffectSpec effect = RasterEffectSpec.DropShadow(1, 14, 14);
        (FilterBuffer plainPixels, Rect2D region) = Plain(Shape(Document()), effect);
        FilterRasteriser.FilteredPicture shadowed = Rendered(Shape(Document(effect)));

        // The top edge's band covers y 55..65. Shifted by (14,14) it covers 69..79, so (110,74) is inside the
        // shadow and inside the rectangle - which has no fill, so the stroke itself draws nothing there.
        (int sx, int sy) = Pixel(shadowed, new Point2D(110, 74));
        (int px, int py) = Pixel(plainPixels, region, new Point2D(110, 74));

        Assert.Equal(0f, plainPixels.AlphaAt(px, py));
        Assert.True(
            shadowed.Pixels.AlphaAt(sx, sy) > 0.5f,
            $"the shadow puts ink at {sx},{sy}: {shadowed.Pixels.AlphaAt(sx, sy)}");

        // The shadow is the colour of the line it falls from, which is red here.
        (float r, float g, float b, float a) = shadowed.Pixels.Get(sx, sy);
        Assert.True(r > g + 0.2f, $"an untinted shadow is the stroke's colour: got ({r}, {g}, {b}) at alpha {a}");

        // ...and it is *displaced*, not a halo: the mirrored point, fourteen units the other way, is empty.
        (int mx, int my) = Pixel(shadowed, new Point2D(110, 46));
        Assert.Equal(0f, shadowed.Pixels.AlphaAt(mx, my));

        // A tint replaces it, which is the other half of "the shadow's colour at known pixels".
        PathItem tintedPath = Shape(Document(
            RasterEffectSpec.DropShadow(1, 14, 14, new ColorRgb(0, 1, 0))));
        FilterRasteriser.FilteredPicture tinted = Rendered(tintedPath);
        (int tx, int ty) = Pixel(tinted, new Point2D(110, 74));
        (float tr, float tg, float _, float _) = tinted.Pixels.Get(tx, ty);
        Assert.True(tg > tr + 0.2f, $"a tinted shadow uses its tint: got ({tr}, {tg})");
    }

    /// <summary>**An outer glow puts ink outside the stroke**, where the stroke itself draws nothing.</summary>
    [Fact]
    public void AnOuterGlowPutsInkOutsideTheStroke()
    {
        ColorRgb gold = new(1, 0.8, 0);
        RasterEffectSpec effect = RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 6, gold);
        (FilterBuffer plainPixels, Rect2D region) = Plain(Shape(Document()), effect);
        FilterRasteriser.FilteredPicture glow = Rendered(Shape(Document(effect)));

        // Three units clear of the band's outer edge at y=55.
        (int ox, int oy) = Pixel(glow, new Point2D(110, 52));
        (int px, int py) = Pixel(plainPixels, region, new Point2D(110, 52));

        Assert.Equal(0f, plainPixels.AlphaAt(px, py));
        Assert.True(
            glow.Pixels.AlphaAt(ox, oy) > 0.05f,
            $"the outer glow reaches {ox},{oy}: {glow.Pixels.AlphaAt(ox, oy)}");

        // ...and it is the glow's own colour, not the line's red.
        (float r, float g, float b, float a) = glow.Pixels.Get(ox, oy);
        Assert.True(g > 0.5f && a > 0.05f, $"the glow is gold: got ({r}, {g}, {b}) at alpha {a}");
    }

    /// <summary>
    /// **An inner glow puts ink inside the stroke and does not escape it**, which is the whole difference between
    /// it and an outer one - asserted at the same pixel, on the same document, so the two answers are comparable.
    ///
    /// The line is drawn **semi-transparent** here on purpose: the graph puts the glow *under* the artwork
    /// (<c>over SourceGraphic, glow</c>), so on an opaque line it is hidden, and the only honest measurement there
    /// is the outside pixel. With a translucent line both halves are visible - the band gains ink, and the outside
    /// stays empty.
    /// </summary>
    [Fact]
    public void AnInnerGlowStaysInsideTheStroke()
    {
        ColorRgb gold = new(1, 0.8, 0);
        ColorRgb translucent = new(1, 0, 0, 0.5);
        RasterEffectSpec inner = RasterEffectSpec.Glow(RasterEffectKind.InnerGlow, 6, gold);
        RasterEffectSpec outer = RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 6, gold);

        PathItem bare = Shape(Document(translucent));
        (FilterBuffer barePixels, Rect2D region) = Plain(bare, inner);

        FilterRasteriser.FilteredPicture inside = Rendered(Shape(Document(translucent, inner)));
        FilterRasteriser.FilteredPicture outside = Rendered(Shape(Document(translucent, outer)));

        // Outside the band's outer edge at y=55: the outer glow reaches it, the inner one must not.
        (int ix, int iy) = Pixel(inside, new Point2D(110, 52));
        (int ox, int oy) = Pixel(outside, new Point2D(110, 52));
        (int ux, int uy) = Pixel(barePixels, region, new Point2D(110, 52));

        Assert.Equal(0f, barePixels.AlphaAt(ux, uy));
        Assert.Equal(0f, inside.Pixels.AlphaAt(ix, iy));
        Assert.True(
            outside.Pixels.AlphaAt(ox, oy) > 0.05f,
            $"the outer glow reaches outside the line: {outside.Pixels.AlphaAt(ox, oy)}");

        // Inside the band the inner glow is drawn: the half-transparent line gains ink, where the bare one has none.
        (int bx, int by) = Pixel(inside, new Point2D(110, 60));
        (int nx, int ny) = Pixel(barePixels, region, new Point2D(110, 60));

        float before = barePixels.AlphaAt(nx, ny);
        float after = inside.Pixels.AlphaAt(bx, by);

        Assert.True(before > 0.4f && before < 0.6f, $"the bare translucent line is about half ink: {before}");
        Assert.True(after > before + 0.02f, $"the inner glow puts ink inside the stroke: {before} to {after}");

        // ...and it is the glow's colour that arrived, not more of the line's red. The gain is small - measured at
        // about a twentieth of an alpha and a twentieth of green on a half-transparent red line - because the glow
        // is composited *under* the artwork, so the colour is what carries the claim: a bare line has **no** green
        // at all here.
        (int gr, int gg) = (nx, ny);
        (float r, float g, float _, float _) = inside.Pixels.Get(bx, by);
        Assert.Equal(0f, barePixels.Get(gr, gg).G, 3);
        Assert.True(g > 0.03f, $"the inner glow is gold inside the line: got ({r}, {g})");
    }

    /// <summary>
    /// **The effect lands where the artwork is, on a page that is not at the document origin.**
    ///
    /// A region is an absolute model-space box, and the artwork is drawn at the artboard's offset. The two agree at
    /// the origin and nowhere else: with the buffer's origin taken from the region's *pixels* rather than its model
    /// rectangle, the drawing window is displaced by the corner's own value, so a stroke six hundred units out is
    /// rasterised entirely outside its bitmap and the export shows nothing at all. This is the export half of the
    /// canvas bug the raster effect work already had to fix twice.
    /// </summary>
    [Fact]
    public void AnEffectAwayFromTheOriginDrawsWhereTheArtworkIs()
    {
        RasterEffectSpec effect = RasterEffectSpec.Blur(6);
        PathItem far = Shape(DocumentAt(540, 460, new ColorRgb(1, 0, 0), effect));

        FilterRasteriser.FilteredPicture blurred = Rendered(far);

        // (650,520) is the centre of the top edge's band, which now runs y 515..525.
        (int cx, int cy) = Pixel(blurred, new Point2D(650, 520));
        float body = blurred.Pixels.AlphaAt(cx, cy);

        Assert.True(body > 0.1f, $"the blurred stroke is not in its own bitmap: {body} at {cx},{cy}");
        Assert.True(Spread(blurred.Pixels) > 0, "nothing at all was rasterised away from the origin");

        // ...and the picture's own rectangle is the region, not a rectangle displaced from it by its own corner.
        Assert.True(
            blurred.Destination.Contains(new Point2D(650, 520)),
            $"the placement {blurred.Destination} does not contain the stroke");
    }

    /// <summary>A radius is a radius: a wider blur covers more pixels than a narrow one on the same stroke.</summary>
    [Fact]
    public void AWiderBlurRadiusSpreadsFurther()
    {
        FilterRasteriser.FilteredPicture narrow = Rendered(Shape(Document(RasterEffectSpec.Blur(2))));
        FilterRasteriser.FilteredPicture wide = Rendered(Shape(Document(RasterEffectSpec.Blur(10))));

        Assert.True(
            Spread(wide.Pixels) > Spread(narrow.Pixels),
            $"radius 10 covered {Spread(wide.Pixels)} pixels and radius 2 covered {Spread(narrow.Pixels)}");
    }

    /// <summary>Two exports of the same document draw the same pixels, or a baseline is not a baseline.</summary>
    [Fact]
    public void TheSameStrokeEffectRasterisesToTheSamePixels()
    {
        RasterEffectSpec[] effects =
        {
            RasterEffectSpec.Blur(3),
            RasterEffectSpec.DropShadow(2, 5, 5),
            RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 4, new ColorRgb(0, 0, 1)),
        };

        FilterRasteriser.FilteredPicture first = Rendered(Shape(Document(effects)));
        FilterRasteriser.FilteredPicture second = Rendered(Shape(Document(effects)));

        Assert.Equal(first.Destination, second.Destination);
        Assert.True(first.Pixels.Matches(second.Pixels, tolerance: 0f), "the same document drew different pixels");
    }

    /// <summary>
    /// **A stroke with no effect costs nothing.** The entry point returns no picture at all, which is what keeps an
    /// ordinary document on the vector path it has always taken - and no note, because there is nothing to report.
    /// </summary>
    [Fact]
    public void AStrokeWithoutEffectsRasterisesToNothing()
    {
        var notes = new List<string>();

        Assert.Null(FilterRasteriser.RasteriseStrokeEffects(
            Shape(Document()), AffineTransform.Identity, 1.0, notes));

        Assert.Empty(notes);
    }

    // ------------------------------------------------------------------ the export, and the gap until it lands

    /// <summary>
    /// The picture the exporter places has to be **the page's picture**, so the two are taken from the same place:
    /// with the declaration false the file must draw exactly the operators an effect-free file draws, and with it
    /// true the file must draw through an image instead. Either way this cannot pass by saying nothing.
    /// </summary>
    [Fact]
    public void AStrokeEffectReachesThePageOrTheGapIsStated()
    {
        string plain = PdfDrawing.Of(PdfDocumentExporter.Export(Document()));
        string affected = PdfDrawing.Of(PdfDocumentExporter.Export(Document(RasterEffectSpec.Blur(6))));

        Assert.True(Flipped == (plain != affected), $"plain:\n{plain}\n\naffected:\n{affected}");

        if (Flipped)
        {
            Assert.DoesNotContain(" Do", plain);
            Assert.Contains(" Do", affected);
        }
    }

    /// <summary>
    /// **The soft edge survives all the way to the bytes.** A blurred stroke is a picture whose coverage is
    /// partial along the whole of its edge, so the exported page must carry an image with an /SMask that has those
    /// partial values in it - "the file mentions an image" and "the file shows the blur" are different claims.
    /// </summary>
    [Fact]
    public void TheExportedPageCarriesTheBlurredStroke()
    {
        byte[] pdf = PdfDocumentExporter.Export(Document(RasterEffectSpec.Blur(6)));

        if (!Flipped)
        {
            // Nothing to assert yet, and asserting it anyway would be the silence this file exists to prevent.
            Assert.Empty(PdfDrawing.Images(pdf));
            return;
        }

        // Two images: the picture and its coverage, which is how PDF carries per-pixel alpha.
        List<PdfDrawing.ImageObject> images = PdfDrawing.Images(pdf);
        Assert.Equal(2, images.Count);

        PdfDrawing.ImageObject picture = Assert.Single(images, image => image.Is("DeviceRGB"));
        PdfDrawing.ImageObject mask = Assert.Single(images, image => image.Is("DeviceGray"));
        Assert.Equal(mask.Number, ((PdfRef)picture.Dict["SMask"]!).Number);

        int partial = mask.Samples.Count(value => value is > 0 and < 255);
        Assert.True(partial > 100, $"the exported blur has only {partial} partially covered pixels");

        // And it is painted: an XObject nothing invokes carries the effect to a file that does not show it.
        string operators = PdfDrawing.Of(pdf);
        Assert.Contains(" cm", operators);
        Assert.Contains(" Do", operators);
    }

    /// <summary>
    /// A radius is a radius in the file too: the wider blur's coverage has more partial pixels than the narrow
    /// one's, read from the exported bytes rather than from the rasteriser that fed them.
    /// </summary>
    [Fact]
    public void AWiderBlurCoversMoreOfTheExportedPage()
    {
        if (!Flipped)
        {
            Assert.Empty(PdfDrawing.Images(PdfDocumentExporter.Export(Document(RasterEffectSpec.Blur(6)))));
            return;
        }

        int narrow = PartialPixels(PdfDocumentExporter.Export(Document(RasterEffectSpec.Blur(2))));
        int wide = PartialPixels(PdfDocumentExporter.Export(Document(RasterEffectSpec.Blur(10))));

        Assert.True(wide > narrow, $"radius 10 covered {wide} partial pixels and radius 2 covered {narrow}");
    }

    /// <summary>The partially covered samples of the page's one coverage mask.</summary>
    private static int PartialPixels(byte[] pdf)
    {
        List<PdfDrawing.ImageObject> images = PdfDrawing.Images(pdf);
        PdfDrawing.ImageObject mask = Assert.Single(images, image => image.Is("DeviceGray"));
        return mask.Samples.Count(value => value is > 0 and < 255);
    }
}
