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
    /// this keeps only the streams that contain drawing operators: a content stream sets a stroke width (`w`) and
    /// the sidecar's JSON does not.
    /// </summary>
    private static string Drawing(byte[] pdf)
    {
        string latin = System.Text.Encoding.Latin1.GetString(pdf);
        var drawing = new System.Text.StringBuilder();

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
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
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
                using var reader = new StreamReader(zlib, System.Text.Encoding.Latin1);
                text = reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                continue;
            }

            if (text.Contains(" w", StringComparison.Ordinal) || text.Contains(" re", StringComparison.Ordinal))
            {
                drawing.Append(text);
            }
        }

        return drawing.ToString();
    }

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
    public void ARasterEffectDoesNotChangeTheFile()
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
    public void AFilterDoesNotChangeTheFile()
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

    [Fact]
    public void ABlendModeDoesNotChangeTheFile()
    {
        Assert.Equal(PdfExportSupport.Find("blendMode")!.Written, Changes(document =>
            Shape(document).BlendMode = BlendMode.Multiply));
    }

    /// <summary>Every feature in the declaration is probed above, so none can be left unverified.</summary>
    [Fact]
    public void EveryDeclaredFeatureIsProbed()
    {
        string[] probed =
        {
            "gradient", "widthProfile", "outlineEffect", "rasterEffect", "filter", "blendMode",
        };

        Assert.Equal(probed.OrderBy(n => n), PdfExportSupport.All.Select(f => f.Name).OrderBy(n => n));
    }

    /// <summary>And the lossy list is what a warning would be drawn from.</summary>
    [Fact]
    public void TheLossyListIsTheOnesThatAreNotWritten()
    {
        Assert.All(PdfExportSupport.Lossy, feature => Assert.False(feature.Written));
        Assert.Contains(PdfExportSupport.Lossy, f => f.Name == "rasterEffect");
        Assert.DoesNotContain(PdfExportSupport.Lossy, f => f.Name == "outlineEffect");
    }
}
