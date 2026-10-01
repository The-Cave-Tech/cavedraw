using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The filter inputs a **renderer** supplies rather than a primitive producing them: `FillPaint`, `StrokePaint` and
/// `BackgroundImage`.
///
/// SVG names all five source inputs in the same breath, but only `SourceGraphic` and `SourceAlpha` can be derived
/// from the shape's own rendering. A shape's fill is not recoverable from the pixels of its fill and stroke painted
/// together, and what is behind an object is not in its own picture at all - so these three are handed to the
/// engine, and a graph that reads one nothing supplied paints transparent. That last case is the failure the issue
/// names, so it is **reported** here rather than left to be discovered from a picture that quietly lost its colour.
/// </summary>
public class FilterSourceInputTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static readonly Rect2D Bounds = new(0, 0, 40, 40);

    private static readonly ColorRgb GraphicColour = new(1, 0, 0);

    private static readonly ColorRgb FillColour = new(0, 1, 0);

    private static readonly ColorRgb StrokeColour = new(0, 0, 1);

    /// <summary>A 20x20 block in the middle of a 40x40 box, in one colour.</summary>
    private static FilterBuffer Block(ColorRgb colour)
    {
        var buffer = new FilterBuffer(40, 40);
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                buffer.Set(x, y, colour, 1f);
            }
        }

        return buffer;
    }

    /// <summary>The same picture rendered over the filter's region, which is the size a source input has to be.</summary>
    private static FilterBuffer InRegion(FilterBuffer source, FilterSpec filter)
    {
        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, Bounds, scale: 1.0);
        var region = new FilterBuffer(width, height);
        region.Blit(source, -x, -y);
        return region;
    }

    /// <summary>A filter that answers with `input` clipped to the shape, which is one step and one colour.</summary>
    private static FilterSpec ClippedToShape(string input, string name)
        => new(name, new[] { FilterPrimitive.Combine("in", input, "SourceAlpha") });

    // ---------------------------------------------------------------- the supplied buffers

    /// <summary>
    /// **`FillPaint` and `StrokePaint` yield the shape's own pictures**, and a step wired to one produces that
    /// colour rather than nothing.
    ///
    /// Each is supplied as the shape rendered in its fill (or stroke) alone, which is exactly what a caller has to
    /// hand over: the engine cannot separate them from the shape's rendering. The same graph with the buffer left
    /// out is asserted beside it, because "produces the fill colour" and "produces nothing" would otherwise be
    /// distinguishable only by eye.
    /// </summary>
    [Theory]
    [InlineData("FillPaint")]
    [InlineData("StrokePaint")]
    public void FillPaintAndStrokePaintYieldTheShapesOwnColour(string input)
    {
        FilterSpec filter = ClippedToShape(input, "wired");
        FilterBuffer graphic = InRegion(Block(GraphicColour), filter);
        ColorRgb paint = input == "FillPaint" ? FillColour : StrokeColour;
        FilterBuffer supplied = InRegion(Block(paint), filter);

        var sources = new FilterSources
        {
            FillPaint = input == "FillPaint" ? supplied : null,
            StrokePaint = input == "StrokePaint" ? supplied : null,
        };

        FilterBuffer wired = new FilterEngine(filter).Evaluate(graphic, Bounds, sources);

        // The middle of the block, which is where both the shape and its paint are.
        (float r, float g, float b, float a) = wired.Get(wired.Width / 2, wired.Height / 2);
        Assert.Equal(1f, a, 3);
        Assert.Equal((float)paint.R, r, 3);
        Assert.Equal((float)paint.G, g, 3);
        Assert.Equal((float)paint.B, b, 3);

        // The colour is the shape's own, not the graphic's - which is what a step reading the wrong buffer gives.
        Assert.NotEqual((float)GraphicColour.R, r, 3);
        Assert.Equal(0f, wired.AlphaAt(0, 0), 3);

        // And with nothing supplied the same graph paints nothing at all, which is the gap being reported.
        var bare = new FilterEngine(filter);
        FilterBuffer nothing = bare.Evaluate(graphic, Bounds);
        Assert.Equal(0, nothing.OpaquePixels());
    }

    /// <summary>
    /// **`BackgroundImage` yields the picture behind the object** when a caller has one - which is the input the
    /// canvas cannot produce, and a compositor or an export can.
    /// </summary>
    [Fact]
    public void BackgroundImageYieldsTheBackdropWhenOneIsSupplied()
    {
        var filter = new FilterSpec("behind", new[]
        {
            FilterPrimitive.OffsetBy(0, 0, input: "BackgroundImage"),
        });

        FilterBuffer graphic = InRegion(Block(GraphicColour), filter);
        FilterBuffer backdrop = InRegion(Block(new ColorRgb(0.2, 0.4, 0.6)), filter);

        var engine = new FilterEngine(filter);
        FilterBuffer result = engine.Evaluate(graphic, Bounds, new FilterSources { BackgroundImage = backdrop });

        // The backdrop is what the step draws, pixel for pixel - not the shape, and not transparency.
        Assert.True(result.Matches(backdrop, 0f), "BackgroundImage must be the picture the caller supplied");
        Assert.Empty(engine.UnsuppliedSourceInputs);

        (float r, float g, float b, float a) = result.Get(result.Width / 2, result.Height / 2);
        Assert.Equal(1f, a, 3);
        Assert.Equal(0.2f, r, 3);
        Assert.Equal(0.4f, g, 3);
        Assert.Equal(0.6f, b, 3);
    }

    // ---------------------------------------------------------------- the gap

    /// <summary>
    /// **An input nothing supplied is named**, so a caller can report it rather than infer it from a picture that
    /// lost its colour. The three are named individually, because which one is missing is the whole of the answer.
    /// </summary>
    [Theory]
    [InlineData("FillPaint")]
    [InlineData("StrokePaint")]
    [InlineData("BackgroundImage")]
    public void AnUnsuppliedSourceInputIsNamed(string input)
    {
        FilterSpec filter = ClippedToShape(input, "wired");

        var engine = new FilterEngine(filter);
        engine.Evaluate(InRegion(Block(GraphicColour), filter), Bounds);

        Assert.Contains(input, engine.UnsuppliedSourceInputs);
    }

    /// <summary>And an input that **was** supplied is not named, so the report is about the gap rather than noise.</summary>
    [Fact]
    public void ASuppliedSourceInputIsNotNamed()
    {
        FilterSpec filter = ClippedToShape("FillPaint", "wired");
        FilterBuffer graphic = InRegion(Block(GraphicColour), filter);

        var engine = new FilterEngine(filter);
        engine.Evaluate(graphic, Bounds, new FilterSources { FillPaint = InRegion(Block(FillColour), filter) });

        Assert.Empty(engine.UnsuppliedSourceInputs);
    }

    /// <summary>
    /// **A file whose filter reads one of them is told at import**, not left to a canvas that will paint it
    /// transparent.
    ///
    /// The canvas has no picture of a shape's fill alone, of its stroke alone, or of anything behind it - so a step
    /// reading one of the three paints nothing there. That is the "artwork quietly went missing" failure this
    /// repository names, and the warning says which input and why.
    /// </summary>
    [Theory]
    [InlineData("FillPaint")]
    [InlineData("StrokePaint")]
    [InlineData("BackgroundImage")]
    public void AFilterReadingAnInputTheCanvasCannotSupplyIsReported(string input)
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\">" +
            $"<feComposite in=\"{input}\" in2=\"SourceAlpha\" operator=\"in\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains(input, StringComparison.Ordinal) &&
            warning.Contains("transparent", StringComparison.Ordinal));

        // The graph is still read faithfully - the input is part of the file's wiring - so the report is a report
        // and not a refusal.
        FilterPrimitive read = Assert.Single(result.Document.FindFilter("f")!.Primitives);
        Assert.Equal(input, read.Input);
    }

    /// <summary>
    /// A graph that reads only `SourceGraphic` and `SourceAlpha` is not reported, which is what stops the warning
    /// being noise on every ordinary filter.
    /// </summary>
    [Fact]
    public void AFilterReadingOnlyTheShapeItselfIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\">" +
            "<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"2\" result=\"soft\"/>" +
            "<feComposite in=\"SourceGraphic\" in2=\"soft\" operator=\"in\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("transparent", StringComparison.Ordinal));
    }

    /// <summary>
    /// The source inputs this build **can** derive are still derived: `SourceAlpha` is the shape's coverage and
    /// `SourceGraphic` its colour, whatever the caller supplies for the other three.
    /// </summary>
    [Fact]
    public void TheTwoDerivedInputsStillComeFromTheShape()
    {
        var filter = new FilterSpec("shape", new[]
        {
            FilterPrimitive.Combine("in", "SourceGraphic", "SourceAlpha"),
        });

        FilterBuffer result = new FilterEngine(filter)
            .Evaluate(InRegion(Block(GraphicColour), filter), Bounds,
                new FilterSources { BackgroundImage = InRegion(Block(new ColorRgb(0, 0, 0)), filter) });

        (float r, float _, float __, float a) = result.Get(result.Width / 2, result.Height / 2);
        Assert.Equal(1f, r, 3);
        Assert.Equal(1f, a, 3);
    }
}
