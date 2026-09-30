using System.Text;
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
        Assert.Empty(path.Clips);
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
    /// </summary>
    [Fact]
    public void ARadialInnerRadiusIsReportedAsAGap()
    {
        byte[] pdf = RadialShadedPdf(innerRadius: 20);

        CadDocument document = PdfImporter.Import(pdf, out IReadOnlyList<string> notes);
        PathItem path = ImportedGradientItem(document);

        Assert.Equal(GradientKind.Radial, path.Fill.Gradient!.Kind);
        Assert.Contains(notes, n => n.Contains("inner radius", StringComparison.OrdinalIgnoreCase));
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

    private static byte[] RadialShadedPdf(double innerRadius)
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
            $"/Coords [110 70 {innerRadius} 110 70 80] /Function 6 0 R /Extend [true true] >>",
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
