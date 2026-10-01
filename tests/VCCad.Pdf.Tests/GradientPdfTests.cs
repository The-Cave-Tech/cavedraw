using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using VCCad.Pdf.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Gradient fills through PDF: export as native shadings (ShadingType 2/3 plus a
/// stitching FunctionType 3 of exponential FunctionType 2 sub-functions) and re-import
/// from <c>/Shading</c> resources and the <c>sh</c> operator.
///
/// The round-trip is the point. A gradient that only survives the lossless sidecar is not
/// real: a foreign viewer must see it, and a foreign PDF's shading must come back as a
/// <see cref="GradientSpec"/>.
/// </summary>
public class GradientPdfTests
{
    private readonly ITestOutputHelper _out;
    public GradientPdfTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------
    // Export: the function shape
    // ------------------------------------------------------------------

    /// <summary>
    /// The stop-pair rule: three stops are TWO adjacent pairs, so the ramp is a
    /// FunctionType 3 stitching two FunctionType 2 sub-functions, with the interior stop's
    /// position as the single /Bounds entry. A test that accepted one sub-function per stop
    /// would pass while the ramp was wrong at every colour boundary.
    /// </summary>
    [Fact]
    public void AThreeStopRampIsTwoStitchingSubFunctionsNotThree()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                new GradientStop(0.5, new ColorRgb(0, 1, 0)),
                new GradientStop(1.0, new ColorRgb(0, 0, 1)),
            },
        };

        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));
        Dictionary<string, object?>? stitch = FindFunction(pdf, 3);

        Assert.NotNull(stitch);
        var functions = Assert.IsType<List<object?>>(stitch!["Functions"]);
        Assert.Equal(2, functions.Count);

        var bounds = Assert.IsType<List<object?>>(stitch["Bounds"]);
        Assert.Single(bounds);
        Assert.Equal(0.5, Convert.ToDouble(bounds[0]), 6);

        // Two sub-functions means two pairs: the first is red→green, the second green→blue.
        Dictionary<string, object?> first = Resolve(pdf, functions[0]);
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, Components(first, "C0"));
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, Components(first, "C1"));
        Dictionary<string, object?> second = Resolve(pdf, functions[1]);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, Components(second, "C0"));
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, Components(second, "C1"));
    }

    /// <summary>
    /// A two-stop gradient is one pair, so it is a bare FunctionType 2 - not a stitching
    /// function with a single sub-function, and certainly not two.
    /// </summary>
    [Fact]
    public void ATwoStopRampIsASingleExponentialFunction()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(0, 0, 0)),
                new GradientStop(1.0, new ColorRgb(1, 1, 1)),
            },
        };

        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));

        Assert.Null(FindFunction(pdf, 3));
        Dictionary<string, object?> alone = FindFunction(pdf, 2)!;
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, Components(alone, "C0"));
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, Components(alone, "C1"));
    }

    /// <summary>
    /// The shading itself: linear is ShadingType 2 and its /Coords are the ramp's ends in
    /// the shape's own space.
    /// </summary>
    [Fact]
    public void ALinearGradientExportsAsAnAxialShading()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Start = new Point2D(0.0, 0.5),
            End = new Point2D(1.0, 0.5),
        };

        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));
        Dictionary<string, object?> shading = FindShading(pdf, 2)!;

        Assert.Equal(2, Convert.ToInt32(shading["ShadingType"]));
        var coords = Assert.IsType<List<object?>>(shading["Coords"]);
        // The square is 20,30..220,130, so the ramp runs from its left-middle to its
        // right-middle in artboard coordinates.
        Assert.Equal(new[] { 20.0, 80.0, 220.0, 80.0 },
            coords.Select(Convert.ToDouble).ToArray());
    }

    // ------------------------------------------------------------------
    // Round trips through the PDF bytes (not the sidecar)
    // ------------------------------------------------------------------

    /// <summary>
    /// Linear: kind, every stop's position and colour, the geometry and the spread survive
    /// export to a real shading and re-import. Uses the vector path explicitly - importing
    /// the exported file normally would restore the lossless sidecar and prove nothing.
    /// </summary>
    [Fact]
    public void ALinearGradientRoundTripsThroughTheShading()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1.0, 0.0, 0.0)),
                new GradientStop(0.35, new ColorRgb(0.0, 0.6, 0.2)),
                new GradientStop(1.0, new ColorRgb(0.0, 0.0, 1.0)),
            },
            Start = new Point2D(0.1, 0.2),
            End = new Point2D(0.9, 0.8),
            Spread = GradientSpread.Pad,
        };

        GradientSpec imported = RoundTrip(gradient);

        Assert.Equal(GradientKind.Linear, imported.Kind);
        Assert.Equal(GradientSpread.Pad, imported.Spread);
        AssertPoint(new Point2D(0.1, 0.2), imported.Start);
        AssertPoint(new Point2D(0.9, 0.8), imported.End);
        AssertStops(gradient.Normalised(), imported.Normalised());
    }

    /// <summary>
    /// Radial: the kind, centre, both radii and rotation survive. The exporter turns the
    /// unit circle into the object's ellipse with the shading CTM, so RadiusX and RadiusY
    /// are genuinely independent rather than one radius shared between them.
    /// </summary>
    [Fact]
    public void ARadialGradientRoundTripsItsCentreAndBothRadii()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1.0, 1.0, 1.0)),
                new GradientStop(1.0, new ColorRgb(0.8, 0.1, 0.1)),
            },
            Center = new Point2D(0.4, 0.6),
            RadiusX = 0.30,
            RadiusY = 0.45,
            Rotation = 0.0,
            Spread = GradientSpread.Pad,
        };

        GradientSpec imported = RoundTrip(gradient);

        Assert.Equal(GradientKind.Radial, imported.Kind);
        AssertPoint(new Point2D(0.4, 0.6), imported.Center);
        Assert.Equal(0.30, imported.RadiusX, 4);
        Assert.Equal(0.45, imported.RadiusY, 4);
        Assert.Equal(0.0, imported.Rotation, 4);
        AssertStops(gradient.Normalised(), imported.Normalised());
    }

    /// <summary>
    /// The gradient is painted through the shape's clip, so the imported item carries the
    /// shape's geometry - not a page-wide rectangle. This is what keeps a gradient inside
    /// the object it fills.
    /// </summary>
    [Fact]
    public void TheGradientIsPaintedInsideTheShapesOwnOutline()
    {
        var gradient = new GradientSpec { Kind = GradientKind.Linear };
        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? round));
        PathItem path = ImportedGradientItem(round!);

        Rect2D box = path.BoundingBox();
        Assert.Equal(20.0, box.Left, 3);
        Assert.Equal(30.0, box.Top, 3);
        Assert.Equal(200.0, box.Width, 3);
        Assert.Equal(100.0, box.Height, 3);

        // The clip is consumed as the shape: it must not also be left attached as a clip,
        // or the same outline would be applied twice and the item would describe a shape
        // it is merely clipped to rather than one it is.
        Assert.Empty(Imported.ClipsOf(path));
    }

    // ------------------------------------------------------------------
    // Unsupported shadings: approximated and reported, never dropped
    // ------------------------------------------------------------------

    /// <summary>
    /// A mesh shading (type 6) has no GradientSpec equivalent. It must not vanish: the
    /// import approximates it with a linear ramp sampled from its function and reports the
    /// approximation.
    /// </summary>
    [Fact]
    public void AnUnsupportedShadingTypeIsApproximatedAndReported()
    {
        byte[] pdf = MeshShadedPdf(6);

        CadDocument document = PdfImporter.Import(pdf, out IReadOnlyList<string> notes);
        PathItem path = ImportedGradientItem(document);

        Assert.True(path.Fill.HasGradient);
        GradientSpec gradient = path.Fill.Gradient!;
        Assert.Equal(GradientKind.Linear, gradient.Kind);
        Assert.True(gradient.Stops.Count >= 2);
        Assert.Contains(notes, n => n.Contains("shading type 6", StringComparison.OrdinalIgnoreCase));
        _out.WriteLine(string.Join(" | ", notes));
    }

    /// <summary>
    /// The same input seen the other way: a type-6 shading with no function has nothing to
    /// sample, and that too is reported rather than silently dropped.
    /// </summary>
    [Fact]
    public void AnUnsupportedShadingWithNoFunctionIsReportedNotSilentlyDropped()
    {
        byte[] pdf = MeshShadedPdf(6, includeFunction: false);

        PdfImporter.Import(pdf, out IReadOnlyList<string> notes);

        Assert.Contains(notes, n => n.Contains("shading type 6", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, n => n.Contains("dropped", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A radial shading with an inner radius cannot be held by the model, so the ramp is
    /// taken from the centre and the gap is reported.
    ///
    /// This is the case that must stay a reported gap: circle 0 is a CIRCLE, not a point, so it
    /// says nothing about where a highlight sits - reading its centre as a focus would invent a
    /// picture the file does not draw. The note is the difference between an approximation and a
    /// silent one.
    /// </summary>
    [Fact]
    public void ARadialInnerRadiusIsReportedAsAGap()
    {
        byte[] pdf = RadialShadedPdf(innerRadius: 20);

        CadDocument document = PdfImporter.Import(pdf, out IReadOnlyList<string> notes);
        PathItem path = ImportedGradientItem(document);

        Assert.Equal(GradientKind.Radial, path.Fill.Gradient!.Kind);
        Assert.Contains(notes, n => n.Contains("inner radius", StringComparison.OrdinalIgnoreCase));
        Assert.Null(path.Fill.Gradient!.FocalPoint);
    }

    // ------------------------------------------------------------------
    // Spread and opacity are gaps: exported, but reported
    // ------------------------------------------------------------------

    /// <summary>
    /// PDF's only real extend is Pad. A Reflect or Repeat gradient is still exported (as
    /// Pad, so the document is not lost) but the caller is told, and the note is the
    /// difference between an approximation and a silently wrong file.
    /// </summary>
    [Fact]
    public void AReflectOrRepeatSpreadIsExportedAsPadAndReported()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Spread = GradientSpread.Reflect,
        };

        byte[] pdf = PdfDocumentExporter.Export(
            DocumentWithSquare(gradient), out IReadOnlyList<string> notes);

        Assert.NotNull(pdf);
        Assert.Contains(notes, n => n.Contains("Reflect", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Stop opacity is not expressible in a shading function. It is not silently dropped:
    /// the export says so.
    /// </summary>
    [Fact]
    public void PerStopOpacityIsExportedAsAReportedGap()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 0, 0), Opacity: 0.0),
                new GradientStop(1.0, new ColorRgb(0, 0, 1), Opacity: 1.0),
            },
        };

        byte[] pdf = PdfDocumentExporter.Export(
            DocumentWithSquare(gradient), out IReadOnlyList<string> notes);

        Assert.NotNull(pdf);
        Assert.Contains(notes, n => n.Contains("opacity", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Radial focal point: the second circle in /Coords
    // ------------------------------------------------------------------

    /// <summary>
    /// A radial's focus is the inner circle of a type 3 shading. `/Coords` is
    /// <c>[x0 y0 r0 x1 y1 r1]</c>, the ramp runs from circle 0 to circle 1, so a focal point
    /// is circle 0 with a radius of ZERO at the focus and circle 1 left as the unit circle
    /// the CTM turns into the object's ellipse.
    ///
    /// The square is 20,30..220,130 and the shading's own space is the unit circle, so the
    /// focus sits at (focus − centre) measured in radii: ((0.25 − 0.5)/0.5, (0.5 − 0.5)/0.5).
    /// A test that only asked whether a focus was *present* would pass on a concentric
    /// shading that happened to write two circles, which is the defect this pins.
    /// </summary>
    [Fact]
    public void AFocusedRadialKeepsItsFocusInTheShadingCoords()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 1, 1)),
                new GradientStop(1.0, new ColorRgb(0, 0, 0)),
            },
            Center = new Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = new Point2D(0.25, 0.5),
        };

        CadDocument document = DocumentWithSquare(gradient);
        byte[] pdf = PdfDocumentExporter.Export(document);
        double[] coords = Coords(FindShading(pdf, 3)!);

        // Circle 0 is the focus as a point, and it is NOT the outer circle's centre.
        Assert.Equal(-0.5, coords[0], 6);
        Assert.Equal(0.0, coords[1], 6);
        Assert.Equal(0.0, coords[2], 6);

        // Circle 1 is untouched: the unit circle the matrix maps to the object's ellipse.
        Assert.Equal(0.0, coords[3], 6);
        Assert.Equal(0.0, coords[4], 6);
        Assert.Equal(1.0, coords[5], 6);

        // And the offset is real geometry, not just a number in a box: read the shading's
        // placement out of the content stream and the focus lands on the model's own point.
        AssertPoint(new Point2D(0.25, 0.5), ShadingPointInBounds(pdf, document, coords[0], coords[1]));
    }

    /// <summary>
    /// The focus is placed through the same circle-to-ellipse matrix as the centre, so it
    /// follows a rotated, anisotropic radial instead of being computed in a frame of its own.
    /// The mistake ruled out here is a focus converted from the model's numbers without the
    /// ellipse's rotation or its two different radii: it lands somewhere else in the box and
    /// still satisfies a naive "is the focus present" assertion. Because the point is read
    /// back into the artboard's own frame, a focus written in the page's y-up numbers instead
    /// would come back mirrored and fail here too.
    /// </summary>
    [Fact]
    public void AFocusedRadialFollowsItsCentreThroughRotationAndThePageFlip()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 1, 1)),
                new GradientStop(1.0, new ColorRgb(0, 0, 0)),
            },
            Center = new Point2D(0.45, 0.55),
            RadiusX = 0.40,
            RadiusY = 0.20,
            Rotation = 30.0,
            FocalPoint = new Point2D(0.30, 0.30),
        };

        CadDocument document = DocumentWithSquare(gradient);
        byte[] pdf = PdfDocumentExporter.Export(document);
        double[] coords = Coords(FindShading(pdf, 3)!);

        AssertPoint(new Point2D(0.45, 0.55), ShadingPointInBounds(pdf, document, coords[3], coords[4]));
        AssertPoint(new Point2D(0.30, 0.30), ShadingPointInBounds(pdf, document, coords[0], coords[1]));

        // The matrix's two columns are the model's radii in artboard units: 0.40 and 0.20 of
        // a 200x100 box, with the rotation carried in the columns rather than in a length.
        double[] matrix = ShadingMatrix(pdf);
        Assert.Equal(80.0, Math.Sqrt((matrix[0] * matrix[0]) + (matrix[1] * matrix[1])), 4);
        Assert.Equal(20.0, Math.Sqrt((matrix[2] * matrix[2]) + (matrix[3] * matrix[3])), 4);
    }

    /// <summary>
    /// A gradient that names no focus - and one that names its own centre - both write the
    /// degenerate concentric form, which is what every document exported before the model
    /// had a focal point wrote. The two centres are asserted EQUAL: "two circles are present"
    /// is exactly what the focused form also satisfies.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0.5)]
    public void ARadialWithNoFocusOrACentredOneWritesTheConcentricForm(double? focusX)
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = focusX is { } x ? new Point2D(x, 0.5) : null,
        };

        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));
        double[] coords = Coords(FindShading(pdf, 3)!);

        Assert.Equal(coords[0], coords[3], 6);
        Assert.Equal(coords[1], coords[4], 6);
        Assert.Equal(0.0, coords[2], 6);
        Assert.Equal(1.0, coords[5], 6);

        // Concentric AND centred, which is the exact form the exporter wrote before this:
        // an existing document's shading bytes do not change.
        Assert.Equal(0.0, coords[0], 6);
        Assert.Equal(0.0, coords[1], 6);
    }

    /// <summary>
    /// PDF requires the inner circle to be strictly inside the outer one. A focus exactly on
    /// the edge - which is what the model holds for a file that put its focus outside, since
    /// the SVG reader clamps it there - is the degenerate case readers are entitled to
    /// misrender, and a focus beyond the edge is worse. It is scaled about the outer circle's
    /// centre until it is inside, along its own ray, so the highlight keeps its direction.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.5)] // on the outer circle's edge
    [InlineData(2.0, 0.9)] // well outside it, and not on an axis
    public void AFocusNotStrictlyInsideIsScaledInsideAlongItsOwnRay(double focusX, double focusY)
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = new Point2D(focusX, focusY),
        };

        byte[] pdf = PdfDocumentExporter.Export(
            DocumentWithSquare(gradient), out IReadOnlyList<string> notes);
        double[] coords = Coords(FindShading(pdf, 3)!);

        double sx = coords[0];
        double sy = coords[1];
        double length = Math.Sqrt((sx * sx) + (sy * sy));
        Assert.True(length < 1.0, $"inner circle at {length} is not strictly inside the outer circle");
        Assert.True(length > 0.99, $"inner circle at {length} was recentred instead of scaled to the edge");

        // Still on the ray from the centre through where the file put it. Four places: the
        // coordinates in the file are rounded to a millionth, which is the floor on how
        // precisely a direction can be read back out of them.
        double ex = (focusX - 0.5) / 0.5;
        double ey = (focusY - 0.5) / 0.5;
        double expected = Math.Sqrt((ex * ex) + (ey * ey));
        Assert.Equal(ex / expected, sx / length, 4);
        Assert.Equal(ey / expected, sy / length, 4);

        // And the file says what it did rather than leaving a reader to notice.
        Assert.Contains(notes, n => n.Contains("focal point", StringComparison.OrdinalIgnoreCase)
                                    && n.Contains("outer circle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// **The other half of the focal point: reading it back.** Our own export writes the focus
    /// as circle 0 of a type 3 shading with a radius of zero, and the vector importer used to
    /// rebuild the radial from circle 1 alone - so export, import gave back a concentric
    /// gradient, and nothing was said about it. The focus is asserted as a COORDINATE and not
    /// as "a focus is present", because the concentric form also writes two circles.
    /// </summary>
    [Fact]
    public void AFocusedRadialSurvivesExportAndVectorImport()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 1, 1)),
                new GradientStop(1.0, new ColorRgb(0, 0, 0)),
            },
            Center = new Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = new Point2D(0.25, 0.5),
        };

        GradientSpec round = RoundTrip(gradient);

        Assert.True(round.FocalPoint is not null, "the focus was dropped by the vector importer");
        Assert.Equal(0.25, round.FocalPoint!.Value.X, 4);
        Assert.Equal(0.5, round.FocalPoint.Value.Y, 4);

        // The focus is a point OFF the centre: a reader that kept the centre as the focus would
        // satisfy "not null" and still paint the wrong picture.
        Assert.NotEqual(round.Center, round.FocalPoint.Value);
    }

    /// <summary>
    /// The same focus carried through a rotated, anisotropic radial, which is where reading
    /// circle 0 without the shading's placement matrix goes wrong: the focus would land in the
    /// page's own frame instead of the object's, and a test that only compared the offset's
    /// LENGTH would not notice.
    /// </summary>
    [Fact]
    public void AFocusedRadialSurvivesTheRoundTripThroughRotationAndAnEllipse()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 1, 1)),
                new GradientStop(1.0, new ColorRgb(0, 0, 0)),
            },
            Center = new Point2D(0.45, 0.55),
            RadiusX = 0.40,
            RadiusY = 0.20,
            Rotation = 30.0,
            FocalPoint = new Point2D(0.30, 0.30),
        };

        GradientSpec round = RoundTrip(gradient);

        Assert.True(round.FocalPoint is not null, "the focus was dropped by the vector importer");
        Assert.Equal(0.30, round.FocalPoint!.Value.X, 3);
        Assert.Equal(0.30, round.FocalPoint.Value.Y, 3);
    }

    /// <summary>
    /// A type 3 shading written by someone else: circle 0 has a radius of ZERO, which is a
    /// point, and a point circle is the focus. Read by hand rather than through our own export,
    /// so a writer and a reader agreeing about the same mistake cannot pass.
    /// </summary>
    [Fact]
    public void AZeroRadiusInnerCircleIsReadAsTheFocus()
    {
        byte[] pdf = RadialShadedPdf(innerRadius: 0, focusX: 130, focusY: 70);

        CadDocument document = PdfImporter.Import(pdf, out IReadOnlyList<string> notes);
        GradientSpec gradient = ImportedGradientItem(document).Fill.Gradient!;

        Assert.Equal(GradientKind.Radial, gradient.Kind);
        Assert.True(gradient.FocalPoint is not null, "a zero-radius inner circle is a focus, not a gap");
        Assert.DoesNotContain(notes, n => n.Contains("inner radius", StringComparison.OrdinalIgnoreCase));

        // 20pt right of the outer centre, whose radius is 80: a quarter of a radius, so the
        // normalised focus is a quarter of RadiusX to the right of the normalised centre.
        Assert.Equal(gradient.Center.X + (0.25 * gradient.RadiusX), gradient.FocalPoint!.Value.X, 4);
        Assert.Equal(gradient.Center.Y, gradient.FocalPoint.Value.Y, 4);
    }

    /// <summary>
    /// A zero-radius inner circle sitting ON the outer centre is the concentric form our own
    /// exporter writes for a gradient with no focus. It must come back as NO focus rather than as
    /// a coordinate, because null and "the centre" are the same picture and only one of them is
    /// what the file said - writing the coordinate back would invent an `fx` the file never had.
    /// </summary>
    [Fact]
    public void AConcentricZeroRadiusShadingComesBackWithNoFocus()
    {
        byte[] pdf = RadialShadedPdf(innerRadius: 0, focusX: 110, focusY: 70);

        CadDocument document = PdfImporter.Import(pdf, out IReadOnlyList<string> notes);
        GradientSpec gradient = ImportedGradientItem(document).Fill.Gradient!;

        Assert.Equal(GradientKind.Radial, gradient.Kind);
        Assert.Null(gradient.FocalPoint);
        Assert.DoesNotContain(notes, n => n.Contains("inner radius", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A focus the SVG reader would clamp - one outside the outer circle - is clamped the same
    /// way by the PDF importer: onto the edge, along the ray from the centre, NOT recentred to
    /// the centre and not kept outside the ellipse. The two readers must agree, or the same
    /// picture imported twice becomes two documents.
    /// </summary>
    [Fact]
    public void AFocusOutsideTheOuterCircleIsClampedToTheEdge()
    {
        // Circle 0 at 910,70 (a whole 10 radii right of the centre) with radius zero.
        byte[] pdf = RadialShadedPdf(innerRadius: 0, focusX: 910, focusY: 70);

        CadDocument document = PdfImporter.Import(pdf, out _);
        GradientSpec gradient = ImportedGradientItem(document).Fill.Gradient!;

        Assert.True(gradient.FocalPoint is not null, "an outside focus must be clamped, not dropped");
        Point2D focus = gradient.FocalPoint!.Value;

        // On the edge: one radius from the centre, in the direction the file put it.
        Assert.Equal(1.0, (focus.X - gradient.Center.X) / gradient.RadiusX, 4);
        Assert.Equal(0.0, focus.Y - gradient.Center.Y, 4);
    }

    /// <summary>
    /// The clamp is SVG's own rule, which is stated for a circle: a focus outside is moved along
    /// the centre-to-focus ray to where that ray meets the edge. Clamping each axis on its own
    /// would answer a different direction, and would still be "on the edge".
    /// </summary>
    [Fact]
    public void AnOutsideFocusIsClampedAlongItsOwnRayRatherThanPerAxis()
    {
        // 600pt right and 400pt down of a centre whose outer radius is 80: in radii that is
        // (7.5, 5), so the clamped point is that direction at one radius - neither axis-aligned
        // nor at the corner a per-axis clamp would have given.
        byte[] pdf = RadialShadedPdf(innerRadius: 0, focusX: 710, focusY: 470);

        CadDocument document = PdfImporter.Import(pdf, out _);
        GradientSpec gradient = ImportedGradientItem(document).Fill.Gradient!;

        Point2D focus = gradient.FocalPoint!.Value;
        double dx = (focus.X - gradient.Center.X) / gradient.RadiusX;
        double dy = (focus.Y - gradient.Center.Y) / gradient.RadiusY;

        // On the edge, pointing the way the file pointed: the 600/400 offset is 3/2 in radii
        // units, so the components keep that ratio rather than both running out to 1.
        Assert.Equal(1.0, Math.Sqrt((dx * dx) + (dy * dy)), 4);
        Assert.Equal(1.5, Math.Abs(dx / dy), 4);
        Assert.True(Math.Abs(dx) > 0.1 && Math.Abs(dy) > 0.1, "the direction must not be axis-aligned");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Freeform and conical have no shading to be written as - a sweep has no /ShadingType at all,
    /// and free-form shadings are meshes - so the field is sampled and the picture is drawn through
    /// the path's own outline. What this pins is that the page carries the picture, clipped to the
    /// shape, rather than silently painting the flat fill colour as it used to.
    /// </summary>
    [Theory]
    [InlineData(GradientKind.Freeform)]
    [InlineData(GradientKind.Conical)]
    public void ASampledGradientIsExportedAsAClippedImage(GradientKind kind)
    {
        GradientSpec gradient = kind == GradientKind.Freeform
            ? new GradientSpec
            {
                Kind = GradientKind.Freeform,
                FreeformMode = FreeformMode.Points,
                Points = new[]
                {
                    new FreeformPoint(new Point2D(30, 40), new ColorRgb(1, 0, 0)),
                    new FreeformPoint(new Point2D(210, 120), new ColorRgb(0, 0, 1)),
                },
            }
            : new GradientSpec
            {
                Kind = GradientKind.Conical,
                Stops = new[]
                {
                    new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                    new GradientStop(1.0, new ColorRgb(0, 0, 1)),
                },
            };

        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));
        string latin = Encoding.Latin1.GetString(pdf);

        // The picture: an RGB image whose grid follows the longer edge of the 200x100 box.
        Assert.Contains("/Subtype /Image", latin, StringComparison.Ordinal);
        Assert.Contains("/ColorSpace /DeviceRGB", latin, StringComparison.Ordinal);
        Assert.Contains("/Width 128", latin, StringComparison.Ordinal);
        Assert.Contains("/Height 64", latin, StringComparison.Ordinal);

        // And it is DRAWN rather than shaded: no shading object exists, and the vector importer finds
        // an image on the page, which it can only do by reading the operator that drew it.
        Assert.DoesNotContain("/ShadingType", latin, StringComparison.Ordinal);
        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? round));
        Assert.Contains(
            round!.Artboards[0].Layers.SelectMany(l => l.Children),
            item => item is ImageItem);
    }

    /// <summary>
    /// The export says what it did: a foreign reader is handed a picture, so anyone opening the file
    /// in another tool should be able to find out why the gradient is not a gradient.
    /// </summary>
    [Fact]
    public void ExportingASampledGradientSaysSo()
    {
        GradientSpec gradient = new()
        {
            Kind = GradientKind.Conical,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                new GradientStop(1.0, new ColorRgb(0, 0, 1)),
            },
        };

        PdfDocumentExporter.Export(DocumentWithSquare(gradient), out IReadOnlyList<string> notes);

        Assert.Contains(notes, n => n.Contains("conical", StringComparison.OrdinalIgnoreCase)
                                    && n.Contains("sampled image", StringComparison.OrdinalIgnoreCase));
    }

    private static CadDocument DocumentWithSquare(GradientSpec gradient)
    {
        CadDocument document = CadDocument.CreateDefault("gradient");
        Layer layer = document.Artboards[0].Layers[0];
        PathItem path = Rect(20, 30, 200, 100);
        path.Fill = FillSpec.WithGradient(gradient);
        path.Stroke = StrokeSpec.None;
        layer.AddItem(path);
        return document;
    }

    private static PathItem Rect(double x, double y, double width, double height)
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(true);
        sub.AppendNode(new Point2D(x, y));
        sub.AppendNode(new Point2D(x + width, y));
        sub.AppendNode(new Point2D(x + width, y + height));
        sub.AppendNode(new Point2D(x, y + height));
        return path;
    }

    private static GradientSpec RoundTrip(GradientSpec gradient)
    {
        byte[] pdf = PdfDocumentExporter.Export(DocumentWithSquare(gradient));

        // Explicitly the vector path: a normal Import would restore the lossless sidecar
        // and the shading would never be exercised.
        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? round));
        return ImportedGradientItem(round!).Fill.Gradient!;
    }

    private static PathItem ImportedGradientItem(CadDocument document)
        => document.Artboards[0].Layers
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .Single(p => p.Fill.HasGradient);

    private static void AssertStops(IReadOnlyList<GradientStop> expected,
        IReadOnlyList<GradientStop> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Position, actual[i].Position, 4);
            Assert.Equal(expected[i].Color.R, actual[i].Color.R, 4);
            Assert.Equal(expected[i].Color.G, actual[i].Color.G, 4);
            Assert.Equal(expected[i].Color.B, actual[i].Color.B, 4);
        }
    }

    private static void AssertPoint(Point2D expected, Point2D actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
    }

    /// <summary>Finds the first function object of a given FunctionType in an exported PDF.</summary>
    private static Dictionary<string, object?>? FindFunction(byte[] pdf, int functionType)
    {
        var file = new PdfFile(pdf);
        foreach (int number in file.ObjectNumbers)
        {
            if (file.GetObject(number) is Dictionary<string, object?> dict &&
                dict.GetValueOrDefault("FunctionType") is double type &&
                (int)type == functionType)
            {
                return dict;
            }
        }

        return null;
    }

    private static Dictionary<string, object?>? FindShading(byte[] pdf, int shadingType)
    {
        var file = new PdfFile(pdf);
        foreach (int number in file.ObjectNumbers)
        {
            if (file.GetObject(number) is Dictionary<string, object?> dict &&
                dict.GetValueOrDefault("ShadingType") is double type &&
                (int)type == shadingType)
            {
                return dict;
            }
        }

        return null;
    }

    /// <summary>The six numbers of a shading's <c>/Coords</c>.</summary>
    private static double[] Coords(Dictionary<string, object?> shading)
        => (shading.GetValueOrDefault("Coords") as List<object?> ?? new List<object?>())
            .Select(Convert.ToDouble).ToArray();

    /// <summary>
    /// A point of the shading's own space, recovered into the model's normalised box from the
    /// bytes: through the <c>cm</c> the content stream set for the shading, and normalised
    /// against the shape's bounds.
    ///
    /// The numbers in a content stream are artboard coordinates, because the page's y-flip is
    /// one outer transform applied to all of them; reading the placement back this way makes
    /// the assertion about where the highlight lands rather than about the arithmetic the
    /// writer happened to use - a focus computed in a frame of its own is what this catches.
    /// </summary>
    private static Point2D ShadingPointInBounds(byte[] pdf, CadDocument document, double sx, double sy)
    {
        double[] m = ShadingMatrix(pdf);
        Point2D artboard = new(
            (sx * m[0]) + (sy * m[2]) + m[4],
            (sx * m[1]) + (sy * m[3]) + m[5]);

        Rect2D box = document.AllPaths().Single().BoundingBox();
        return new Point2D(
            (artboard.X - box.Left) / box.Width,
            (artboard.Y - box.Top) / box.Height);
    }

    /// <summary>
    /// The <c>cm</c> the content stream sets immediately before it paints the shading. The
    /// page's y-flip is a separate, earlier <c>cm</c>, so the nearest preceding one is the
    /// shading's own placement.
    /// </summary>
    private static double[] ShadingMatrix(byte[] pdf)
    {
        foreach (string content in InflatedStreams(pdf))
        {
            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Trim().EndsWith(" sh", StringComparison.Ordinal))
                {
                    continue;
                }

                for (int j = i - 1; j >= 0; j--)
                {
                    string line = lines[j].Trim();
                    if (line.EndsWith(" cm", StringComparison.Ordinal))
                    {
                        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Take(6)
                            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
                            .ToArray();
                    }
                }
            }
        }

        throw new InvalidOperationException("the file paints no shading, so it has no placement matrix.");
    }

    /// <summary>Every FlateDecode stream's plain text, which is where the operators are.</summary>
    private static IEnumerable<string> InflatedStreams(byte[] pdf)
    {
        string latin = Encoding.Latin1.GetString(pdf);

        foreach (Match match in Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = match.Index + match.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            string text;
            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
                using var reader = new StreamReader(zlib, Encoding.Latin1);
                text = reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                continue;
            }

            yield return text;
        }
    }

    private static Dictionary<string, object?> Resolve(byte[] pdf, object? reference)
    {
        var file = new PdfFile(pdf);
        return file.ResolveDict(reference) ?? new Dictionary<string, object?>();
    }

    private static double[] Components(Dictionary<string, object?> function, string key)
        => (function.GetValueOrDefault(key) as List<object?> ?? new List<object?>())
            .Select(Convert.ToDouble).ToArray();

    /// <summary>
    /// A page carrying a shading of <paramref name="shadingType"/> painted through a
    /// rectangle clip, so an unsupported type can be exercised end to end.
    /// </summary>
    private static byte[] MeshShadedPdf(int shadingType, bool includeFunction = true)
    {
        const string content = "q 10 20 200 100 re W n /Sh0 sh Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /Shading << /Sh0 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            includeFunction
                ? "<< /ShadingType " + shadingType + " /ColorSpace /DeviceRGB /Function 6 0 R >>"
                : "<< /ShadingType " + shadingType + " /ColorSpace /DeviceRGB >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>",
        };

        return Assemble(objects);
    }

    private static byte[] RadialShadedPdf(double innerRadius, double focusX = 110, double focusY = 70)
    {
        const string content = "q 10 20 200 100 re W n /Sh0 sh Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /Shading << /Sh0 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /ShadingType 3 /ColorSpace /DeviceRGB " +
            $"/Coords [{focusX} {focusY} {innerRadius} 110 70 80] /Function 6 0 R /Extend [true true] >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 0] /N 1 >>",
        };

        return Assemble(objects);
    }

    private static byte[] Assemble(List<string> objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}
