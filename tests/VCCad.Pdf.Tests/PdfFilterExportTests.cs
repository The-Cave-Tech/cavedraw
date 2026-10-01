using System.IO.Compression;
using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The last unmet acceptance point on the filter issue: a filter that reaches the canvas must reach the **PDF
/// export** too, or the export must say what it dropped.
///
/// A filter is a raster operation, so the honest way to carry one in a vector file is the way a raster effect is
/// carried - the object is drawn into pixels, the graph runs over them, and the answer is placed as an image. That
/// is what these tests assert, and they assert it on **parsed PDF objects** rather than on a byte count: an image
/// XObject with the filter's region for its placement, a content stream that paints through it, and coverage in
/// the mask that is genuinely soft where the unfiltered shape was hard.
///
/// The gap this closes was recorded in the exporter rather than discovered from a file
/// (<see cref="PdfExportSupport"/> said <c>filter: false</c>), so <see cref="AFilteredShapeDrawsThroughTheFilterOrTheGapIsStated"/>
/// carries both halves and is written against the declaration itself.
/// </summary>
public class PdfFilterExportTests
{
    /// <summary>
    /// Whether the exporter declares the filter written - **the one thing that flips between the gap and its
    /// closing**, read from the declaration rather than written down twice. With it false the first test asserts
    /// the old behaviour (a filtered export draws the same operators as an unfiltered one) and the others stand
    /// down; with it true the same tests assert the picture.
    /// </summary>
    private static bool Flipped => PdfExportSupport.Find("filter")!.Written;

    private static CadDocument Document()
    {
        CadDocument document = CadDocument.CreateDefault("Filter export");

        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(60, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(140, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(140, 110)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 110)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    /// <summary>The same document with a blur over an alpha-driven drop shadow - a filter whose whole point is
    /// pixels, since a blur cannot be written as geometry.</summary>
    private static CadDocument Filtered()
    {
        CadDocument document = Document();
        document.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(6, input: "SourceAlpha", result: "blurred"),
            FilterPrimitive.OffsetBy(4, 4, input: "blurred", result: "offset"),
            FilterPrimitive.Combine("over", "offset", "SourceGraphic", "shadowed"),
        })
        {
            Output = "shadowed",
        });

        document.AllPaths().Single().FilterId = "soft";
        return document;
    }

    /// <summary>
    /// The page content streams, decoded, concatenated.
    ///
    /// Found through the page tree rather than by looking for a stream whose bytes happen to look like operators:
    /// the file also carries the ICC profile, the sidecar and the XMP packet, and the ICC profile's bytes are
    /// compressed image-like data that a substring search happily matches. Naming what is asked for - the content
    /// streams - is the only filter that cannot be fooled by a later stream being added.
    /// </summary>
    private static string Operators(byte[] pdf) => PdfDrawing.Of(pdf);

    /// <summary>An image XObject in a file, with the object number its dictionary was parsed from.</summary>
    private static List<PdfDrawing.ImageObject> Images(byte[] pdf) => PdfDrawing.Images(pdf);

    // ------------------------------------------------------------------ the gap, and its closing

    [Fact]
    public void AFilteredShapeDrawsThroughTheFilterOrTheGapIsStated()
    {
        string plain = Operators(PdfDocumentExporter.Export(Document()));
        string filtered = Operators(PdfDocumentExporter.Export(Filtered()));

        Assert.True(Flipped == (plain != filtered), $"plain:\n{plain}\n\nfiltered:\n{filtered}");

        if (Flipped)
        {
            Assert.DoesNotContain(" Do", plain);
            Assert.Contains(" Do", filtered);
        }
    }

    [Fact]
    public void TheExportedFilterIsAnImageXObjectTheContentStreamPaints()
    {
        byte[] pdf = PdfDocumentExporter.Export(Filtered());

        if (!Flipped)
        {
            // Nothing to assert yet, and asserting it anyway would be the silence this file exists to prevent.
            Assert.Empty(Images(pdf));
            return;
        }

        // Two images: the picture and its coverage. PDF has no per-pixel alpha in a colour image, so a filter's
        // alpha is carried the way a transparency channel is - a greyscale /SMask beside the colour samples.
        List<PdfDrawing.ImageObject> images = Images(pdf);
        Assert.Equal(2, images.Count);

        PdfDrawing.ImageObject picture = Assert.Single(images, image => image.Is("DeviceRGB"));
        PdfDrawing.ImageObject mask = Assert.Single(images, image => image.Is("DeviceGray"));

        Assert.Equal(8.0, picture.NumberValue("BitsPerComponent"), 6);
        Assert.Equal(8.0, mask.NumberValue("BitsPerComponent"), 6);

        // Same grid, and the mask is *part of* the picture rather than a second picture: without the reference
        // the colour samples would paint the blur's soft edge as an opaque grey box.
        Assert.Equal(picture.NumberValue("Width"), mask.NumberValue("Width"), 6);
        Assert.Equal(picture.NumberValue("Height"), mask.NumberValue("Height"), 6);
        Assert.Equal(mask.Number, ((PdfRef)picture.Dict["SMask"]!).Number);

        // The colour samples are three bytes per pixel, which is what "straight RGB, mask beside it" means.
        Assert.Equal(
            3 * (int)(picture.NumberValue("Width") * picture.NumberValue("Height")),
            picture.Samples.Length);

        // And it is painted: an XObject nothing invokes carries the filter to a file that does not show it.
        string operators = Operators(pdf);
        Assert.Contains(" cm", operators);
        Assert.Contains(" Do", operators);
    }

    /// <summary>
    /// **The rendered result differs.** A shape whose filter changes the picture has to produce a page that shows
    /// the change, not merely a file that mentions it - so this reads the coverage the page paints and checks that
    /// the blur has spread the shape's edge out.
    ///
    /// An unfiltered rectangle has one alpha value per covered pixel, all of it 255: a hard edge. The blurred page
    /// has a wide band of partial values around it, which is what a blur is.
    /// </summary>
    [Fact]
    public void TheFilteredPageShowsTheBlurTheUnfilteredOneDoesNot()
    {
        if (!Flipped)
        {
            return;
        }

        PdfDrawing.ImageObject mask = Assert.Single(
            Images(PdfDocumentExporter.Export(Filtered())), image => image.Is("DeviceGray"));

        int partial = mask.Samples.Count(value => value is > 0 and < 255);
        int opaque = mask.Samples.Count(value => value == 255);

        Assert.True(partial > 200, $"the blurred page had only {partial} partially covered pixels");
        Assert.True(mask.Samples.Max() > 240, $"the blurred page's strongest pixel was {mask.Samples.Max()}");
    }

    /// <summary>
    /// The rasteriser on its own: the shape's own pixels are solid, and the filter changes them.
    ///
    /// Asserted on the **values** rather than on the file structure, because "the export mentions a filter" and
    /// "the export draws what the filter draws" are different claims and only the second one closes the gap.
    /// </summary>
    [Fact]
    public void TheRasterisedShapeIsSolidBeforeTheFilterAndSoftAfter()
    {
        CadDocument document = Filtered();
        PathItem path = document.AllPaths().Single();
        FilterSpec filter = document.FindFilter("soft")!;

        FilterBuffer? source = FilterRasteriser.SourceGraphic(path, filter, path.WorldBounds());
        Assert.NotNull(source);

        // The shape's own pixels, found where the region has them rather than at a guessed centre - the whole
        // point of this half is that the picture exists at all.
        Rect2D region = FilterRasteriser.RegionOf(filter, path.WorldBounds());
        (int insideX, int insideY) = Pixel(source!, region, new Point2D(100, 95));
        (int outsideX, int outsideY) = Pixel(source!, region, new Point2D(53, 56));

        Assert.True(
            source.AlphaAt(insideX, insideY) > 0.9f,
            $"the shape's own pixel was {source.AlphaAt(insideX, insideY)} at {insideX},{insideY} of " +
            $"{source.Width}x{source.Height} for region {region}");

        // And nothing was drawn outside it, which is what makes the spread after the filter the filter's doing.
        Assert.Equal(0f, source.AlphaAt(outsideX, outsideY));

        FilterRasteriser.FilteredPicture? result = FilterRasteriser.Rasterise(
            path, filter, path.WorldBounds(), AffineTransform.Identity, 1.0, new List<string>());

        Assert.NotNull(result);
        Assert.Equal(source.Width, result!.Value.Pixels.Width);
        Assert.Equal(source.Height, result.Value.Pixels.Height);

        // The **margin** is where the blur shows: the graph composites the blurred copy over the original, so
        // the shape's body stays opaque and being soft there would be a different filter. A pixel outside the
        // shape has nothing at all before the filter and part of a shadow after it.
        (int marginX, int marginY) = Margin(source, region, result.Value.Pixels, path.WorldBounds());

        Assert.Equal(0f, source.AlphaAt(marginX, marginY));

        float after = result.Value.Pixels.AlphaAt(marginX, marginY);
        Assert.True(after > 0f, $"the filter spread nothing outside the shape ({marginX},{marginY})");
        Assert.True(after < 1f, $"the filter made the margin fully opaque at {after}");
    }

    /// <summary>A world point as a pixel in the region's own grid.</summary>
    private static (int X, int Y) Pixel(FilterBuffer buffer, Rect2D region, Point2D world)
    {
        double scale = buffer.Width / region.Width;
        return (
            (int)Math.Round((world.X - region.X) * scale),
            (int)Math.Round((world.Y - region.Y) * scale));
    }

    /// <summary>
    /// A pixel the shape does not cover and the filter does - part of the shadow's falloff.
    ///
    /// Found by asking rather than by guessing a coordinate: the filter's own region decides how far the effect
    /// reaches, and a test that named a point by hand would be pinning this shape's arithmetic rather than the
    /// behaviour. Searched just outside the shape's own box, which is where a shadow is.
    /// </summary>
    private static (int X, int Y) Margin(
        FilterBuffer source, Rect2D region, FilterBuffer filtered, Rect2D shape)
    {
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                double worldX = region.X + ((x + 0.5) * region.Width / source.Width);
                double worldY = region.Y + ((y + 0.5) * region.Height / source.Height);

                bool outside = worldX < shape.Left || worldX > shape.Right ||
                               worldY < shape.Top || worldY > shape.Bottom;

                if (outside && source.AlphaAt(x, y) <= 0.001f && filtered.AlphaAt(x, y) > 0.01f)
                {
                    return (x, y);
                }
            }
        }

        // Nowhere at all, which the caller reports as "the filter spread nothing".
        return (0, 0);
    }

    // ------------------------------------------------------------------ the honest refusals

    /// <summary>
    /// **`FillPaint` is supplied, not read as transparent.** SVG names it as the shape drawn in its own fill, and a
    /// renderer is the only thing that can produce it: the pixels of a filled and stroked shape are the two painted
    /// together, so there is no way back from them to either one - which is why the engine takes it from the caller
    /// rather than deriving it. The canvas hands it over (`FilterCanvasTests` pins that half), so an export that
    /// left it out would draw a filter evaluated against nothing and disagree with the drawing exactly where the
    /// effect is.
    ///
    /// Asserted on the **samples** rather than on the note, because "the export mentions FillPaint" and "the export
    /// drew the fill" are different claims and only the second one is a picture.
    /// </summary>
    [Fact]
    public void AFilterReadingTheFillPaintIsGivenTheShapesOwnFill()
    {
        if (!Flipped)
        {
            return;
        }

        CadDocument document = Document();
        PathItem shape = document.AllPaths().Single();
        shape.Fill = FillSpec.Solid(new ColorRgb(1, 0, 0));

        document.AddFilter(new FilterSpec("filling", new[]
        {
            FilterPrimitive.Combine("in", "FillPaint", "SourceAlpha"),
        }));

        shape.FilterId = "filling";

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        // Supplied rather than omitted, so there is nothing left to report.
        Assert.DoesNotContain(notes, note => note.Contains("FillPaint", StringComparison.Ordinal));

        PdfDrawing.ImageObject picture = Assert.Single(Images(pdf), image => image.Is("DeviceRGB"));

        int red = 0;
        int green = 0;
        for (int i = 0; i + 2 < picture.Samples.Length; i += 3)
        {
            red = Math.Max(red, picture.Samples[i]);
            green = Math.Max(green, picture.Samples[i + 1]);
        }

        // Reading the fill as transparency would leave every sample at zero and the claim below would fail.
        Assert.True(red > 200, $"the fill's own colour never reached the pixels: red peaked at {red}");
        Assert.True(green < 40, $"the picture is not the fill's colour: green peaked at {green}");
    }

    /// <summary>
    /// A fill that is **not one colour** cannot be rasterised into the graph's input: this exporter writes a
    /// gradient as a shading, which has no single colour to composite, so the shape's own pixels could not be
    /// produced at all. The artwork is therefore exported as the vectors it really is - unfiltered - and the reason
    /// is reported. A filtered picture with the fill missing would be the worse lie, because the shape itself would
    /// go missing rather than merely its effect.
    /// </summary>
    [Fact]
    public void AFilteredShapeWithAGradientFillIsReportedRatherThanDrawnWithoutIt()
    {
        if (!Flipped)
        {
            return;
        }

        CadDocument document = Document();
        PathItem shape = document.AllPaths().Single();
        shape.Fill = shape.Fill with
        {
            Gradient = new GradientSpec
            {
                Kind = GradientKind.Linear,
                Stops = new[]
                {
                    new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                    new GradientStop(1.0, new ColorRgb(0, 0, 1)),
                },
            },
        };

        document.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(6, input: "SourceGraphic"),
        }));

        shape.FilterId = "soft";

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        Assert.Contains(notes, note => note.Contains("gradient", StringComparison.OrdinalIgnoreCase));

        // "Exported unfiltered" is not a phrase to take on trust: the page has to draw what the same document
        // draws with no filter on it, gradient and all.
        CadDocument plain = Document();
        PathItem plainShape = plain.AllPaths().Single();
        plainShape.Fill = shape.Fill;
        plain.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(6, input: "SourceGraphic"),
        }));

        Assert.Equal(Operators(PdfDocumentExporter.Export(plain)), Operators(pdf));
    }

    /// <summary>
    /// A graph that reads the picture behind the object cannot be written honestly: the exporter walks items one at
    /// a time and has no backdrop raster to hand over, so the input would read as transparent and the page would
    /// show a filter that was evaluated against nothing. The artwork is exported unfiltered **and the reason is
    /// reported**, which is the difference between a stated gap and a lost effect.
    /// </summary>
    [Fact]
    public void AFilterReadingTheBackdropIsReportedRatherThanPaintedWrong()
    {
        CadDocument document = Document();
        document.AddFilter(new FilterSpec("behind", new[]
        {
            FilterPrimitive.Blur(4, input: "BackgroundImage"),
        }));

        document.AllPaths().Single().FilterId = "behind";

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        if (!Flipped)
        {
            Assert.Empty(notes);
            return;
        }

        Assert.Contains(notes, note => note.Contains("BackgroundImage", StringComparison.Ordinal));
        Assert.Empty(Images(pdf));
    }

    /// <summary>
    /// A region past what this build will allocate is refused rather than turned into a bitmap nobody asked for -
    /// the same limit the canvas applies - and the refusal names the reason. Without this an exporter that
    /// allocated whatever a file said would be a memory exhaustion waiting in a shape's numbers.
    /// </summary>
    [Fact]
    public void ARegionTooLargeToAllocateIsReportedRatherThanAllocated()
    {
        CadDocument document = Document();
        document.AddFilter(new FilterSpec("huge", new[]
        {
            FilterPrimitive.Blur(4, input: "SourceGraphic"),
        })
        {
            ObjectBoundingBox = false,
            X = 0,
            Y = 0,
            Width = 20000,
            Height = 20000,
        });

        document.AllPaths().Single().FilterId = "huge";

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        if (!Flipped)
        {
            Assert.Empty(notes);
            return;
        }

        Assert.Contains(notes, note => note.Contains("past the", StringComparison.Ordinal));
        Assert.Empty(Images(pdf));
    }
}
