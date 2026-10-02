using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// What the PDF export carries, **derived from the file** rather than taken on trust.
///
/// A feature is "written" when turning it on changes the exported bytes, and not when it does not. This compares two
/// exports that differ only by the feature and checks the answer against <see cref="PdfExportSupport"/> - so the
/// declaration cannot drift. The day someone teaches the exporter to rasterise a blur, this fails, and the warning a
/// panel shows stops being a lie.
///
/// It also means the declarations were **discovered**, not assumed. If something I believed was written turns out
/// not to be, this says so rather than a person finding out from an exported file.
/// </summary>
public class PdfExportSupportTests
{
    /// <summary>A document with one rectangle: a black fill and a visible stroke, so every feature has something to
    /// attach to.</summary>
    private static CadDocument Document()
    {
        CadDocument document = CadDocument.CreateDefault();

        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 40)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 6, StrokeCap.Butt, StrokeJoin.Miter, 4));

        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    private static PathItem Shape(CadDocument document) => document.AllPaths().Single();

    private static byte[] Export(CadDocument document) => PdfDocumentExporter.Export(document);

    /// <summary>Whether the feature changes what the file **draws**.</summary>
    private static bool Changes(Action<CadDocument> apply)
    {
        string plain = Drawing(Export(Document()));

        CadDocument document = Document();
        apply(document);

        return plain != Drawing(Export(document));
    }

    /// <summary>
    /// The drawing, which is **not** the whole file.
    ///
    /// The export embeds the lossless sidecar, so changing any model value changes the bytes - a raster effect
    /// changes the file while changing nothing that is drawn. Asking "did the file change" would therefore answer
    /// yes for every feature and the test would prove nothing. What a fidelity warning is about is the picture, so
    /// this keeps only the page content streams, found through the page tree rather than by looking for bytes that
    /// happen to look like operators.
    /// </summary>
    private static string Drawing(byte[] pdf) => PdfDrawing.Of(pdf);

    [Fact]
    public void AGradientChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("gradient")!.Written, Changes(document =>
            Shape(document).Fill = Shape(document).Fill with
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
            }));
    }

    [Fact]
    public void AWidthProfileChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("widthProfile")!.Written, Changes(document =>
        {
            PathItem shape = Shape(document);
            shape.Strokes[0] = shape.Strokes[0] with { WidthProfile = WidthProfileSpec.Taper(20, 2) };
        }));
    }

    [Fact]
    public void AnOutlineEffectChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("outlineEffect")!.Written, Changes(document =>
        {
            PathItem shape = Shape(document);
            shape.Strokes[0] = shape.Strokes[0] with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(3, seed: 7) }),
            };
        }));
    }

    [Fact]
    public void ARasterEffectChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("rasterEffect")!.Written, Changes(document =>
        {
            PathItem shape = Shape(document);
            shape.Strokes[0] = shape.Strokes[0] with
            {
                RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
            };
        }));
    }

    [Fact]
    public void AFilterChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("filter")!.Written, Changes(document =>
        {
            document.AddFilter(new FilterSpec("soft", new[]
            {
                FilterPrimitive.Blur(8, input: "SourceGraphic"),
            }));

            Shape(document).FilterId = "soft";
        }));
    }

    /// <summary>A blend mode is composited now: the mode becomes an ExtGState and the item switches to it.</summary>
    [Fact]
    public void ABlendModeChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("blendMode")!.Written, Changes(document =>
            Shape(document).BlendMode = BlendMode.Multiply));
    }

    /// <summary>A stroke's blend is composited the same way, and it is the same declaration.</summary>
    [Fact]
    public void AStrokeBlendModeChangesTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("blendMode")!.Written, Changes(document =>
        {
            PathItem shape = Shape(document);
            shape.Strokes[0] = shape.Strokes[0] with { Blend = BlendMode.Screen };
        }));
    }

    /// <summary>
    /// **A group's blend is not written**, and this is the derived half of that declaration. CSS composites a group
    /// as a unit, which PDF needs an isolated transparency group for - and that is a form XObject this exporter does
    /// not emit. Blending each child separately would be a different picture, so the file is unchanged and the
    /// declaration says so.
    /// </summary>
    [Fact]
    public void AGroupBlendModeDoesNotChangeTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("blendModeGroup")!.Written, Changes(document =>
        {
            PathItem shape = Shape(document);
            var group = new ArtGroup { Name = "group" };
            document.Artboards[0].Layers[0].RemoveItem(shape);
            group.AddItem(shape);
            document.Artboards[0].Layers[0].AddItem(group);
            group.BlendMode = BlendMode.Multiply;
        }));
    }

    /// <summary>Every feature in the declaration is probed above, so none can be left unverified.</summary>
    [Fact]
    public void EveryDeclaredFeatureIsProbed()
    {
        string[] probed =
        {
            "gradient", "widthProfile", "outlineEffect", "rasterEffect", "filter", "blendMode", "blendModeGroup",
        };

        Assert.Equal(probed.OrderBy(n => n), PdfExportSupport.All.Select(f => f.Name).OrderBy(n => n));
    }

    /// <summary>And the lossy list is what a warning would be drawn from.</summary>
    [Fact]
    public void TheLossyListIsTheOnesThatAreNotWritten()
    {
        Assert.All(PdfExportSupport.Lossy, feature => Assert.False(feature.Written));
        Assert.DoesNotContain(PdfExportSupport.Lossy, f => f.Name == "outlineEffect");

        // A leaf item's blend and a stroke's are composited now, as an ExtGState the paint switches to...
        Assert.DoesNotContain(PdfExportSupport.Lossy, f => f.Name == "blendMode");

        // ...but a group's is not, because that needs a transparency group this exporter cannot emit.
        Assert.Contains(PdfExportSupport.Lossy, f => f.Name == "blendModeGroup");

        // The filter is no longer one of them: the exporter draws the graph's answer and places it.
        Assert.DoesNotContain(PdfExportSupport.Lossy, f => f.Name == "filter");

        // Nor is a stroke's raster effect: it is rasterised into an image XObject from the stroke side.
        Assert.DoesNotContain(PdfExportSupport.Lossy, f => f.Name == "rasterEffect");
    }
}
