using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The SVG writer.
///
/// Asserted two ways: the **attributes in the file**, because a stroke that exports as a plain centred line is the
/// silent substitution this exporter exists to avoid, and by **importing the result and comparing the model**, which
/// is the only test that says the file means what the document meant.
/// </summary>
public class SvgWriterTests
{
    private static PathItem Line(StrokeSpec stroke, FillSpec? fill = null)
    {
        var path = new PathItem { Name = "line", Fill = fill ?? FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(10, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(110, 20)));
        path.Stroke = stroke;
        return path;
    }

    private static CadDocument Document(params PathItem[] paths)
    {
        CadDocument document = CadDocument.CreateDefault();
        foreach (PathItem path in paths)
        {
            document.Artboards[0].Layers[0].AddItem(path);
        }

        return document;
    }

    private static string Write(CadDocument document) => SvgWriter.Write(document);

    private static CadDocument RoundTrip(CadDocument document) => SvgReader.Read(SvgWriter.Write(document)).Document;

    /// <summary>The dump without item ids, which are regenerated on import and differ for that reason alone.</summary>
    private static string Dump(CadDocument document)
        => Regex.Replace(ModelDump.Of(document), @"id=[0-9a-fA-F-]{36}", "id=<id>");

    // ---------------------------------------------------------------- stroke fidelity

    /// <summary>**Every stroke member SVG can carry**, read back from the exported file.</summary>
    [Fact]
    public void EveryStrokeMemberSurvivesTheFile()
    {
        var stroke = new StrokeSpec(
            true, new ColorRgb(0.2, 0.4, 0.6), 7.5, StrokeCap.Round, StrokeJoin.Bevel, 9,
            StrokeAlignment.Center, new DashPattern(new[] { 3.0, 1.5 }, 2.25));

        string svg = Write(Document(Line(stroke)));

        Assert.Contains("stroke-width=\"7.5\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-linecap=\"round\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-linejoin=\"bevel\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-dasharray=\"3 1.5\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-dashoffset=\"2.25\"", svg, StringComparison.Ordinal);
        Assert.Contains("#336699", svg, StringComparison.Ordinal);
    }

    /// <summary>The same members, through the reader - which is what says the file is understood, not just written.</summary>
    [Fact]
    public void EveryStrokeMemberSurvivesARoundTrip()
    {
        var stroke = new StrokeSpec(
            true, new ColorRgb(0.2, 0.4, 0.6, 0.5), 7.5, StrokeCap.Round, StrokeJoin.Bevel, 9,
            StrokeAlignment.Center, new DashPattern(new[] { 3.0, 1.5 }, 2.25));

        StrokeSpec back = RoundTrip(Document(Line(stroke)))
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.Equal(7.5, back.Width, 6);
        Assert.Equal(StrokeCap.Round, back.Cap);
        Assert.Equal(StrokeJoin.Bevel, back.Join);
        Assert.Equal(9.0, back.MiterLimit, 6);
        Assert.Equal(new[] { 3.0, 1.5 }, back.Dash.Segments.ToArray());
        Assert.Equal(2.25, back.Dash.Offset, 6);
        Assert.Equal(0.2, back.Color.R, 3);
        Assert.Equal(0.5, back.Color.A, 3);
    }

    /// <summary>A miter limit at the default is not written, because it is what a viewer assumes.</summary>
    [Fact]
    public void TheDefaultMiterLimitIsNotWritten()
    {
        string svg = Write(Document(Line(new StrokeSpec(
            true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4))));

        Assert.DoesNotContain("stroke-miterlimit", svg, StringComparison.Ordinal);
    }

    /// <summary>**A dashed stroke keeps its phase** - the member most likely to be dropped, and the one that
    /// decides where the pattern starts.</summary>
    [Fact]
    public void ADashKeepsItsPhase()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, new DashPattern(new[] { 4.0, 2.0 }, 3.5));

        StrokeSpec back = RoundTrip(Document(Line(stroke)))
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.Equal(3.5, back.Dash.Offset, 6);
    }

    // ---------------------------------------------------------------- what SVG cannot carry

    /// <summary>
    /// **An aligned stroke becomes a path, not a centred stroke.** SVG has no aligned stroke, and quietly writing
    /// a centred one would put half the paint on the wrong side of the line.
    /// </summary>
    [Fact]
    public void AnAlignedStrokeIsClippedRatherThanCentred()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Inside);

        string svg = Write(Document(Line(stroke)));

        Assert.Contains("clip-path", svg, StringComparison.Ordinal);
        Assert.Contains("<clipPath", svg, StringComparison.Ordinal);

        // And it is drawn at double width, so the clipped half is the width that was asked for.
        Assert.Contains("stroke-width=\"8\"", svg, StringComparison.Ordinal);
    }

    /// <summary>**A width profile produces outlines**, not a plain stroke - because SVG cannot vary a width.</summary>
    [Fact]
    public void AWidthProfileBecomesOutlines()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, default, WidthProfileSpec.Taper(20, 2));

        string svg = Write(Document(Line(stroke)));

        // Filled rather than stroked, and no stroke-width anywhere near it.
        Assert.Contains("fill=\"#000000\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-width", svg, StringComparison.Ordinal);
    }

    /// <summary>And an outline effect is written the same way, for the same reason.</summary>
    [Fact]
    public void AnEffectBecomesOutlines()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Black, 6, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 4) }),
        };

        Assert.DoesNotContain("stroke-width", Write(Document(Line(stroke))), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fills and gradients

    /// <summary>A gradient is written once and referred to, so a document that shares one writes one definition.</summary>
    [Fact]
    public void AGradientIsWrittenOnceAndReferredTo()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Spread = GradientSpread.Pad,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                new GradientStop(1.0, new ColorRgb(0, 0, 1)),
            },
        };

        CadDocument document = Document(
            Line(new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4),
                FillSpec.Solid(ColorRgb.Black) with { Gradient = gradient }),
            Line(new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4),
                FillSpec.Solid(ColorRgb.Black) with { Gradient = gradient }));

        string svg = Write(document);

        Assert.Equal(1, Regex.Matches(svg, "<linearGradient").Count);
        Assert.Equal(2, Regex.Matches(svg, "fill=\"url\\(#grad1\\)\"").Count);
    }

    /// <summary>**The round trip returns the same gradient geometry**, which #119 could not test without this.</summary>
    [Fact]
    public void AGradientSurvivesARoundTrip()
    {
        var gradient = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Spread = GradientSpread.Reflect,
            Center = new Point2D(0.25, 0.75),
            RadiusX = 0.4,
            RadiusY = 0.4,
            Stops = new[]
            {
                new GradientStop(0.0, new ColorRgb(1, 1, 0), 0.5),
                new GradientStop(1.0, new ColorRgb(0, 0.5, 0)),
            },
        };

        CadDocument document = Document(Line(
            new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4),
            FillSpec.Solid(ColorRgb.Black) with { Gradient = gradient }));

        GradientSpec back = RoundTrip(document)
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Fill.Gradient!;

        Assert.Equal(GradientKind.Radial, back.Kind);
        Assert.Equal(GradientSpread.Reflect, back.Spread);
        Assert.Equal(0.25, back.Center.X, 4);
        Assert.Equal(0.75, back.Center.Y, 4);
        Assert.Equal(2, back.Stops.Count);
        Assert.Equal(0.5, back.Stops[0].Opacity, 3);
    }

    /// <summary>A document with no strokes at all exports as a plain filled path, unchanged by this feature.</summary>
    [Fact]
    public void ADocumentWithNoStrokesIsWrittenAsAFill()
    {
        var path = new PathItem { Name = "square", Fill = FillSpec.Solid(new ColorRgb(0, 1, 0)) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        string svg = Write(Document(path));

        Assert.Contains("fill=\"#00ff00\"", svg, StringComparison.Ordinal);
        Assert.Contains(" Z\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-width", svg, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the round trip

    /// <summary>
    /// **Import, export, import, compare.** The issue's own rule for what "the pair is finished" means: for every
    /// element the importer reads, the model has to come back the same.
    /// </summary>
    [Fact]
    public void ADocumentSurvivesTheThreeWayRoundTrip()
    {
        const string Source =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">" +
            "<style>rect { fill: #ff0000; }</style>" +
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">" +
            "<stop offset=\"0\" stop-color=\"#00ff00\"/><stop offset=\"1\" stop-color=\"#0000ff\"/>" +
            "</linearGradient></defs>" +
            "<g transform=\"translate(10,5)\">" +
            "<rect id=\"first\" x=\"0\" y=\"0\" width=\"50\" height=\"30\"/>" +
            "<path id=\"second\" d=\"M0 0 L40 10 C50 20 60 20 70 10 Z\" fill=\"url(#g)\" " +
            "stroke=\"#000000\" stroke-width=\"3\" stroke-dasharray=\"4 2\" stroke-dashoffset=\"1\"/>" +
            "</g></svg>";

        CadDocument first = SvgReader.Read(Source).Document;
        CadDocument second = SvgReader.Read(SvgWriter.Write(first)).Document;

        Assert.Equal(Dump(first), Dump(second));
    }

    /// <summary>The same, for a real file - one of Inkscape's own, which is what the corpus is for.</summary>
    [Fact]
    public void ACorpusFileSurvivesTheThreeWayRoundTrip()
    {
        string? path = CorpusFile("style-parsing.svg");
        if (path is null)
        {
            return;
        }

        CadDocument first = SvgReader.ReadFile(path).Document;
        CadDocument second = SvgReader.Read(SvgWriter.Write(first)).Document;

        Assert.Equal(Dump(first), Dump(second));
    }

    private static string? CorpusFile(string name)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            foreach (string candidate in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string? found = Directory.GetFiles(candidate, name).FirstOrDefault();
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Unreadable, and not needed.
                }
            }
        }

        return null;
    }
}
