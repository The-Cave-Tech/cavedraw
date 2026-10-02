using System.Globalization;
using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The round trip the export issue is really about: **import, export, import, and compare the model.**
///
/// Every other test in this suite asks whether the writer emitted something. This one asks whether the document
/// that comes back is the document that went in - which is a different question, and the one that finds the losses
/// nobody notices. The #117 case is the archetype: the writer disclosed an instance's link, the reader never read
/// it back, and an instance became a silently-downgraded copy whose geometry was *right*. A loss that leaves the
/// picture correct cannot be seen by looking at the picture, so it has to be asserted.
///
/// **The dump.** A canonical text dump of the part of the model the SVG reader and writer speak: artboards, their
/// layers, and every item's structure, geometry, paint, foreign baggage and text runs. Colours are dumped through
/// the same 8-bit quantisation the file states them with, because that is a documented conversion rather than a
/// loss; every other number is dumped at full precision and compared with a tolerance only for the error that
/// composing a transform introduces.
///
/// **What is excused, and how.** A raster round-trips now - the writer emits an `image` at its placement with the
/// bytes as a data URI - so it is part of the dump like everything else: an <see cref="ImageItem"/> is compared on
/// its grid, its colour space, its samples, its mask and its palette, which is the issue's own acceptance rule
/// ("the re-imported model, not that bytes were written"). What a raster cannot carry is asserted on the report
/// instead: an image the writer could not encode must be **named**, and the corpus theory checks that no raster is
/// left out of the file in silence. Nothing else is excused - a difference is a failure, not a tolerance to widen.
/// </summary>
public class SvgRoundTripTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
        "xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd\" " +
        "width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    /// <summary>The three-way trip, which is the only assertion that says the file means what it meant.</summary>
    private static (CadDocument Before, CadDocument After, SvgWriteResult Result) RoundTrip(string body)
    {
        SvgImportResult first = Read(body);
        SvgWriteResult result = SvgWriter.WriteResult(first.Document);
        return (first.Document, SvgReader.Read(result.Svg).Document, result);
    }

    /// <summary>Imports, exports and re-imports done, asserting the two models are the same document.</summary>
    private static SvgWriteResult AssertSurvives(string body)
    {
        (CadDocument before, CadDocument after, SvgWriteResult result) = RoundTrip(body);
        AssertSameDump(Dump(before), Dump(after));
        return result;
    }

    // ---------------------------------------------------------------- the numbers are the numbers

    /// <summary>
    /// **A coordinate is written as the number it is, not as a rendering of it.**
    ///
    /// The writer used to format every number with `0.#####`, so a node the file placed at 73.836398 came back at
    /// 73.8364 - five decimals, which is 0.00004pt of movement on a path node. It is invisible: the drawing is
    /// identical, nothing is reported, and only a comparison of the two models can see it. A transform composed
    /// from a view-box fit loses more than the fifth decimal, which is what made it worth asserting rather than
    /// shrugging at.
    ///
    /// **Before:** the value came back rounded, and this assertion failed on every node of the path.
    /// </summary>
    [Fact]
    public void ACoordinateSurvivesTheRoundTrip()
    {
        (CadDocument before, CadDocument after, _) = RoundTrip(
            "<path d=\"M 73.836398,-277.57016 C 75.203739,-129.89737 81.781969,0 205.03239,0\"/>");

        PathNode original = before.AllPaths().Single().SubPaths[0].Nodes[0];
        PathNode returned = after.AllPaths().Single().SubPaths[0].Nodes[0];

        Assert.Equal(73.836398, original.Anchor.X);
        Assert.Equal(original.Anchor.X, returned.Anchor.X, 12);
        Assert.Equal(original.Anchor.Y, returned.Anchor.Y, 12);

        // And the curve's own handles, because a control point that moves is a different curve.
        Assert.Equal(original.OutHandle.X, returned.OutHandle.X, 12);
        Assert.Equal(original.OutHandle.Y, returned.OutHandle.Y, 12);
        Assert.Equal(original.InHandle, returned.InHandle);
    }

    /// <summary>
    /// The same rule for a transform, which is where the rounding was worst: a transform is a matrix of long
    /// decimals, and `0.#####` rewrote the frame everything inside it sits in.
    /// </summary>
    [Fact]
    public void ATransformSurvivesTheRoundTrip()
    {
        (CadDocument before, CadDocument after, string svg) = RoundTripTouchingExport(
            "<g id=\"frame\" transform=\"matrix(2.834645669291339,0,0,2.834645669291339," +
            "-5.684341886080802E-14,0.10000000000000001)\"><rect width=\"10\" height=\"10\"/></g>");

        ArtGroup frame = before.AllGroups().Single(g => g.Name == "frame");
        Assert.Contains("2.834645669291339", svg, StringComparison.Ordinal);

        ArtGroup returned = after.AllGroups().Single(g => g.Name == "frame");
        Assert.Equal(frame.Transform.A, returned.Transform.A, 12);
        Assert.Equal(frame.Transform.E, returned.Transform.E, 12);
        Assert.Equal(frame.Transform.F, returned.Transform.F, 12);
    }

    // ---------------------------------------------------------------- the baggage

    /// <summary>
    /// **A path with one ordinary stroke keeps its namespaced attributes.**
    ///
    /// This is the #117 shape of defect, found by the dump comparison rather than by looking at anything. The
    /// writer puts the file's `inkscape:`/`sodipodi:` baggage back through `ApplyForeign`, and that call sat inside
    /// the `else` of `if (strokes.Count == 1)`. So a path with exactly one natively-writable stroke - the common
    /// case, and what every Inkscape file is made of - was written with none of its foreign attributes: the
    /// `sodipodi:nodetypes` a hand-edited path carried, the `inkscape:connector-curvature`, the label. A path with
    /// no stroke kept them, and a path with two strokes kept them, which is why no single-stroke-free test ever saw
    /// it.
    ///
    /// **Before:** `a.svg`, `b.svg` and `d.svg` in the Inkscape corpus each lost every `sodipodi:nodetypes` on
    /// their paths across one round trip, and the file looked exactly the same.
    /// </summary>
    [Fact]
    public void APathWithASingleStrokeKeepsItsNamespacedAttributes()
    {
        SvgImportResult first = Read(
            "<path id=\"path5\" sodipodi:nodetypes=\"sssss\" inkscape:connector-curvature=\"0\" " +
            "style=\"fill:none;stroke:#1a1a1a;stroke-width:47.228\" d=\"M 10,20 L 110,20\"/>");

        PathItem path = first.Document.AllPaths().Single();
        Assert.Equal("sssss", path.ForeignAttributes["sodipodi:nodetypes"]);
        Assert.Single(path.Strokes, stroke => stroke.HasVisibleOutline);

        string svg = SvgWriter.Write(first.Document);
        Assert.Contains("sodipodi:nodetypes=\"sssss\"", svg, StringComparison.Ordinal);
        Assert.Contains("inkscape:connector-curvature=\"0\"", svg, StringComparison.Ordinal);

        Assert.Equal("sssss", SvgReader.Read(svg).Document.AllPaths().Single().ForeignAttributes["sodipodi:nodetypes"]);
    }

    /// <summary>
    /// **And the writer does not invent a label the file did not have.**
    ///
    /// The name of an element is carried by its `id`, which the reader reads back as the name - so writing an
    /// `inkscape:label` as well inverts the file's own fact. Every object in the corpus whose element carried no
    /// label came back with one, and the model gained a foreign attribute on a round trip that had lost none. Where
    /// the file *did* carry a label, the name still wins, because that is the member a rename moves.
    /// </summary>
    [Fact]
    public void TheWriterDoesNotInventALabel()
    {
        (CadDocument before, CadDocument after, string svg) = RoundTripTouchingExport(
            "<path id=\"path5\" fill=\"none\" stroke=\"#000000\" d=\"M 10,20 L 110,20\"/>");

        Assert.DoesNotContain("inkscape:label", svg, StringComparison.Ordinal);
        Assert.Equal("path5", after.AllPaths().Single().Name);
        Assert.DoesNotContain("inkscape:label", before.AllPaths().Single().ForeignAttributes.Keys);
        Assert.DoesNotContain("inkscape:label", after.AllPaths().Single().ForeignAttributes.Keys);
    }

    /// <summary>
    /// **A label the file did carry survives, and a rename moves it.**
    ///
    /// The other half of the rule above: not inventing one must not mean dropping the one that exists.
    /// </summary>
    [Fact]
    public void ALabelTheFileCarriedSurvivesAndFollowsARename()
    {
        SvgImportResult first = Read("<path id=\"path5\" inkscape:label=\"bodice\" d=\"M 0,0 L 10,10\"/>");
        PathItem path = first.Document.AllPaths().Single();
        Assert.Equal("bodice", path.Name);

        string svg = SvgWriter.Write(first.Document);
        Assert.Contains("inkscape:label=\"bodice\"", svg, StringComparison.Ordinal);
        Assert.Equal("bodice", SvgReader.Read(svg).Document.AllPaths().Single().Name);

        // A rename goes to the label, not to a stale one beside it.
        path.Name = "bodice front";
        string renamed = SvgWriter.Write(first.Document);
        Assert.Contains("inkscape:label=\"bodice front\"", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("inkscape:label=\"bodice\"", renamed, StringComparison.Ordinal);
        Assert.Equal("bodice front", SvgReader.Read(renamed).Document.AllPaths().Single().Name);
    }

    // ---------------------------------------------------------------- the styles

    /// <summary>
    /// **A `fill-rule` survives even where there is no fill to apply it to.**
    ///
    /// A shape set to `fill:none` still states its rule, and the reader reads it. The writer only wrote
    /// `fill-rule` when it had a fill to write as well, so the value came back as the default `NonZero`. The
    /// picture is identical - there is no fill for a rule to apply to - which is exactly why it is worth pinning:
    /// it is a value the file stated and the round trip silently replaced.
    ///
    /// **Before:** `reference.svg` in the corpus came back `EvenOdd -> NonZero` on its unfilled rectangle.
    /// </summary>
    [Fact]
    public void AFillRuleSurvivesWithoutAFill()
    {
        (CadDocument before, CadDocument after, string svg) = RoundTripTouchingExport(
            "<rect width=\"10\" height=\"10\" style=\"fill:none;fill-rule:evenodd;stroke:#000000\"/>");

        Assert.Equal(FillRule.EvenOdd, before.AllPaths().Single().Fill.Rule);
        Assert.Contains("fill-rule=\"evenodd\"", svg, StringComparison.Ordinal);
        Assert.Equal(FillRule.EvenOdd, after.AllPaths().Single().Fill.Rule);
    }

    /// <summary>
    /// **A hatch fill is named rather than quietly drawn as a solid.**
    ///
    /// The model's fill can be ruled lines and this writer has no `pattern` output, so `FillAttribute` falls back
    /// to the fill's own colour. A solid where the document drew hatching is a plausible substitute - the answer
    /// this exporter is forbidden to give quietly - so it goes on the report. The SVG reader never builds a hatch,
    /// so this can only fire on a document this editor made.
    /// </summary>
    [Fact]
    public void AHatchFillIsReportedRatherThanDrawnAsASolid()
    {
        CadDocument document = CadDocument.CreateDefault();
        var path = new PathItem { Name = "hatched" };
        path.Fill = new FillSpec(true, ColorRgb.Black, FillRule.NonZero,
            null, HatchSpec.Single(45.0, 4.0));
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        document.Artboards[0].Layers[0].AddItem(path);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        string entry = Assert.Single(result.Missing);
        Assert.StartsWith("path 'hatched'", entry, StringComparison.Ordinal);
        Assert.Contains("hatching", entry, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the text

    /// <summary>
    /// **White space the file meant literally comes back unchanged.**
    ///
    /// The reader collapses white space the way the specification says, and it collapses with
    /// `char.IsWhiteSpace` - which is Unicode white space and not just the four XML characters. A French
    /// typesetter's non-breaking space before `!` is therefore collapsed to an ordinary one unless the writer asks
    /// for the spaces to be kept. The line moves by a fraction of a space, so the picture is right and nothing
    /// says so.
    ///
    /// **Before:** `test-peppercarrot-text.svg` came back with `Zwiff\u00A0!` as `Zwiff !` in eight runs.
    /// </summary>
    [Theory]
    [InlineData("Zwiff\u00A0!")]
    [InlineData("a\u00A0b\u00A0c")]
    [InlineData("tab\there")]
    public void LiteralSpaceSurvivesTheRoundTrip(string content)
    {
        var text = new TextItem { Origin = new Point2D(10, 20), Color = ColorRgb.Black };
        text.Runs.Add(new TextRun { Text = content, FontFamily = "Nimbus Sans", FontSize = 12 });

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(text);

        string svg = SvgWriter.Write(document);
        Assert.Contains("xml:space=\"preserve\"", svg, StringComparison.Ordinal);

        TextItem returned = SvgReader.Read(svg).Document.AllItems().OfType<TextItem>().Single();
        Assert.Equal(content, returned.PlainText);
    }

    /// <summary>
    /// And an ordinary single interior space must **not** make the writer ask for preservation: a file that says
    /// `xml:space="preserve"` on every ordinary block would stop the reader collapsing the indentation a person
    /// never sees, which is a different bug in the other direction.
    /// </summary>
    [Fact]
    public void AnOrdinarySpaceDoesNotAskForPreservation()
    {
        var text = new TextItem { Origin = new Point2D(10, 20) };
        text.Runs.Add(new TextRun { Text = "hello world", FontFamily = "Nimbus Sans", FontSize = 12 });

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(text);

        string svg = SvgWriter.Write(document);
        Assert.DoesNotContain("xml:space", svg, StringComparison.Ordinal);
        Assert.Equal("hello world",
            SvgReader.Read(svg).Document.AllItems().OfType<TextItem>().Single().PlainText);
    }

    // ---------------------------------------------------------------- one file per feature

    /// <summary>Shapes and paths with their styles, which is where the round trip starts.</summary>
    [Fact]
    public void ShapesAndStylesSurviveTheRoundTrip()
    {
        AssertSurvives(
            "<rect id=\"box\" x=\"10\" y=\"20\" width=\"30\" height=\"40\" rx=\"3\" " +
            "style=\"fill:#204080;fill-opacity:0.5;stroke:#800000;stroke-width:2.5;stroke-linecap:round;" +
            "stroke-linejoin:bevel;stroke-miterlimit:7;stroke-dasharray:4 2 1;stroke-dashoffset:1.5\"/>");
    }

    /// <summary>`use`/`symbol`, whose link is the thing #117 found was not being read back.</summary>
    [Fact]
    public void AnInstanceSurvivesTheRoundTripAsAnInstance()
    {
        (CadDocument before, CadDocument after, _) = RoundTrip(
            "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" x=\"50\" y=\"30\"/>");

        Assert.Equal(
            before.AllGroups().Where(g => g.SourceId is not null).Select(g => g.SourceId),
            after.AllGroups().Where(g => g.SourceId is not null).Select(g => g.SourceId));
    }

    /// <summary>An Inkscape live path effect, which is a definition the path refers to by id.</summary>
    [Fact]
    public void APathEffectSurvivesTheRoundTrip()
    {
        const string Effect =
            "<inkscape:path-effect xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "effect=\"spiro\" id=\"pe1\" is_visible=\"true\" lpeversion=\"1\"/>";

        SvgImportResult first = Read(
            "<defs>" + Effect + "</defs>" +
            "<path id=\"s\" inkscape:path-effect=\"#pe1\" d=\"M 10,20 L 110,20\"/>");

        Assert.Contains(first.Document.AllPaths().Single().ForeignElements,
            xml => xml.Contains("spiro", StringComparison.Ordinal));

        SvgWriteResult result = SvgWriter.WriteResult(first.Document);
        Assert.Contains("spiro", result.Svg, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(result.Svg);
        Assert.Contains(again.Document.AllPaths().Single().ForeignElements,
            xml => xml.Contains("spiro", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the whole corpus

    /// <summary>
    /// **Every file in the Inkscape corpus is the same document after a round trip.**
    ///
    /// This is the issue's own acceptance rule at the size that makes it worth running: twenty-eight real
    /// drawings made by a real editor, each imported, exported and imported again, with the two models compared
    /// line for line. Hand-written fixtures test the cases someone thought of; the corpus tests the rest.
    ///
    /// The two things that are excused are the two the repository has decided on. A raster is written and its
    /// bytes are part of the comparison; a raster the writer could **not** state - a CMYK scan, a mask that is still
    /// compressed - is named, and that naming is asserted here rather than assumed: a document that dropped an image
    /// without saying so fails this test even though the image is not in the file. Everything else must match, with
    /// a tolerance only for the last bits of a double recomposed through a transform.
    ///
    /// **Before:** ten of the twenty-eight were the identity. The rest lost coordinates to five-decimal rounding,
    /// every `sodipodi:nodetypes` on a single-stroke path, a `fill-rule` on an unfilled shape, or a non-breaking
    /// space.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryCorpusFileRoundTripsItsModel(string path)
    {
        // The skip sentinel: a theory that yields no data is a CI error, so an absent corpus is one empty case.
        if (path.Length == 0)
        {
            return;
        }

        SvgImportResult first;
        try
        {
            first = SvgReader.ReadFile(path);
        }
        catch (SvgImportException exception)
        {
            Assert.Fail($"{Path.GetFileName(path)}: {exception.Message}");
            return;
        }

        SvgWriteResult result = SvgWriter.WriteResult(first.Document);
        SvgImportResult again = SvgReader.Read(result.Svg);

        AssertSameDump(Dump(first.Document), Dump(again.Document), Path.GetFileName(path));

        // **No raster is lost in silence.** A picture the writer could state is in the file - and the dump above
        // compares its bytes - so what is left for the report is the raster it could not state. Every image in the
        // document either reached an `image` element or is named, and a writer that dropped one without saying so
        // fails here even though the file looks fine. This used to assert that the count of images EQUALLED the
        // count of reports, which was true only while every raster was unwritable.
        int images = CountImages(first.Document);
        int written = result.ByElement.GetValueOrDefault("image");
        int named = result.Missing.Count(entry => entry.StartsWith("image ", StringComparison.Ordinal));

        Assert.True(named >= images - written,
            $"{Path.GetFileName(path)}: {images} raster(s) in the document, {written} in the file, {named} named - " +
            "an image the writer leaves out has to be named rather than dropped");
    }

    /// <summary>
    /// Every raster in the document, **definitions included**.
    ///
    /// <see cref="CadDocument.AllItems"/> walks the artboards and the pasteboard, and a raster can also live in
    /// `defs` - where the writer puts it so an instance's link still names something after a save - so counting only
    /// the artwork would make a definition that the writer left out invisible to this assertion.
    /// </summary>
    private static int CountImages(CadDocument document)
    {
        // `AllItems` is already the flattened artwork, so it is counted once - recursing into it as well would
        // count every image inside a group once per ancestor it sits under.
        int count = document.AllItems().OfType<ImageItem>().Count();
        foreach (LayerItem entry in document.Definitions.Children)
        {
            count += CountImagesIn(entry);
        }

        return count;
    }

    private static int CountImagesIn(LayerItem item)
    {
        int count = item is ImageItem ? 1 : 0;
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                count += CountImagesIn(child);
            }
        }

        return count;
    }

    /// <summary>The corpus, wherever it was fetched to; one empty case when it is absent.</summary>
    public static IEnumerable<object[]> CorpusFiles()
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");

        if (!Directory.Exists(cache))
        {
            return new[] { new object[] { string.Empty } };
        }

        var found = new List<string>();
        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            try
            {
                found.AddRange(Directory.GetFiles(directory, "*.svg", SearchOption.AllDirectories));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable corpus directory is one this test does not need.
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found.Count == 0
            ? new[] { new object[] { string.Empty } }
            : found.Select(file => new object[] { file });
    }

    // ---------------------------------------------------------------- the dump

    /// <summary>
    /// A canonical dump of the model the SVG reader and writer speak, as lines a person can diff.
    ///
    /// Colours go through the same 8-bit quantisation the file states them with, because that is the writer's
    /// documented conversion and a dump that ignored it would report every colour as a loss. A raster is dumped by
    /// its grid and its bytes, because the picture *is* the samples and a colour read from one corner would pass on
    /// a raster whose components had been read out of place.
    /// </summary>
    private static List<string> Dump(CadDocument document)
    {
        var lines = new List<string>();

        foreach (Artboard artboard in document.Artboards)
        {
            lines.Add($"artboard {N(artboard.X)} {N(artboard.Y)} {N(artboard.Width)} {N(artboard.Height)} " +
                $"visible={artboard.IsVisible}");

            foreach (Layer layer in artboard.Layers)
            {
                lines.Add($"  layer '{layer.Name}' visible={layer.IsVisible} locked={layer.IsLocked} " +
                    $"opacity={N(layer.Opacity)}");
                DumpItems(layer.Children, "    ", lines);
            }
        }

        DumpItems(document.Orphans.Children, "  pasteboard ", lines);
        return lines;
    }

    private static void DumpItems(IEnumerable<LayerItem> items, string indent, List<string> lines)
    {
        foreach (LayerItem item in items)
        {
            string common =
                $"{indent}{item.GetType().Name} '{item.Name}' visible={item.IsVisible} locked={item.IsLocked} " +
                $"blend={item.BlendMode} filter={item.FilterId ?? "-"} clips={item.Clips.Count} " +
                $"foreign={Foreign(item)}";

            switch (item)
            {
                case ArtGroup group:
                    lines.Add($"{common} transform={Transform(group.Transform)} opacity={N(group.Opacity)} " +
                        $"source={group.SourceId ?? "-"}");
                    DumpItems(group.Children, indent + "  ", lines);
                    break;

                case PathItem path:
                    lines.Add($"{common} opacity={N(path.Opacity)} fill={Fill(path.Fill)} " +
                        $"strokes={string.Join(";", path.Strokes.Select(Stroke))} " +
                        $"subpaths={SubPaths(path)}");
                    break;

                case TextItem text:
                    lines.Add($"{common} origin={Point(text.Origin)} align={text.Alignment} " +
                        $"lineSpacing={N(text.LineSpacing)} colour={Colour(text.Color)} " +
                        $"runs={Runs(text)}");
                    break;

                // **A raster is dumped by its bytes.** The picture is the sample grid, and a comparison that read
                // one colour would pass on a raster whose components had been read out of place - which for a
                // sub-byte sample is not a rounding, it is the wrong pixel. The samples, the mask and the palette
                // are therefore compared as hex, and the packing, the bit depth and the colour space beside them.
                case ImageItem image:
                    lines.Add($"{common} placement={Rect(image.Placement)} " +
                        $"pixels={image.PixelWidth}x{image.PixelHeight} bits={image.BitsPerComponent} " +
                        $"space={image.ColorSpace} base={image.PaletteBase} codec={image.Filter ?? "-"} " +
                        $"maskCodec={image.MaskFilter ?? "-"} mirror={image.MirrorX}/{image.MirrorY} " +
                        $"samples={Hex(image.Samples)} mask={Hex(image.Mask)} palette={Hex(image.Palette)} " +
                        $"decode={Numbers(image.Decode)} key={Numbers(image.ColourKey)}");
                    break;

                default:
                    lines.Add(common);
                    break;
            }
        }
    }

    private static string Rect(Rect2D rect)
        => $"({N(rect.X)},{N(rect.Y)},{N(rect.Width)},{N(rect.Height)})";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static string Numbers(double[]? values)
        => values is null ? "-" : string.Join(",", values.Select(N));

    private static string Foreign(LayerItem item)
    {
        IEnumerable<string> attributes = item.ForeignAttributes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}");

        return "[" + string.Join(",", attributes) + "]/" + item.ForeignElements.Count;
    }

    private static string Fill(FillSpec fill)
    {
        string gradient = fill.Gradient is { } spec
            ? $"gradient({spec.Kind},{spec.Spread},{Point(spec.Start)},{Point(spec.End)},{Point(spec.Center)}," +
              $"{N(spec.RadiusX)},{N(spec.RadiusY)},{Stops(spec)})"
            : "-";
        string hatch = fill.Hatch is { } hatchSpec
            ? "hatch(" + string.Join(",", hatchSpec.Lines.Select(line =>
                $"{N(line.AngleDegrees)}/{N(line.Spacing)}/{N(line.Width)}")) + ")"
            : "-";

        return $"{fill.IsVisible}/{fill.Rule}/{Colour(fill.Color)}/{gradient}/{hatch}";
    }

    private static string Stops(GradientSpec gradient)
        => string.Join(",", gradient.Stops.Select(stop =>
            $"{N(stop.Position)}:{Colour(stop.Color)}:{N(stop.Opacity)}"));

    private static string Stroke(StrokeSpec stroke)
        => $"{Colour(stroke.Color)}@{N(stroke.Width)}/{stroke.Cap}/{stroke.Join}/{N(stroke.MiterLimit)}/" +
           $"{string.Join(" ", stroke.Dash.Segments.Select(N))}+{N(stroke.Dash.Offset)}/{stroke.Alignment}";

    private static string SubPaths(PathItem path)
        => string.Join("|", path.SubPaths.Select(sub =>
            (sub.IsClosed ? "C" : "O") + string.Join("", sub.Nodes.Select(node =>
                $"[{Point(node.Anchor)}{Point(node.InHandle)}{Point(node.OutHandle)}]"))));

    private static string Runs(TextItem text)
        => string.Join("|", text.Runs.Select(run =>
            $"{Escape(run.Text)}:{run.FontFamily}:{N(run.FontSize)}:{(run.Bold ? "b" : "-")}:" +
            $"{(run.Italic ? "i" : "-")}:{Colour(run.Color ?? text.Color)}:{N(run.LetterSpacing)}:{N(run.WordSpacing)}"));

    private static string Point(Point2D point) => $"({N(point.X)},{N(point.Y)})";

    /// <summary>The colour as the file states it: eight bits a channel, which is the writer's documented conversion.</summary>
    private static string Colour(ColorRgb colour)
        => $"#{Byte(colour.R):x2}{Byte(colour.G):x2}{Byte(colour.B):x2}/{N(colour.A)}";

    private static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255);

    private static string Transform(AffineTransform transform)
        => $"({N(transform.A)},{N(transform.B)},{N(transform.C)},{N(transform.D)},{N(transform.E)},{N(transform.F)})";

    /// <summary>The shortest spelling that reads back as the same double, which is what the writer emits too.</summary>
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) && c != ' ')
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static (CadDocument Before, CadDocument After, string Svg) RoundTripTouchingExport(string body)
    {
        SvgImportResult first = Read(body);
        string svg = SvgWriter.Write(first.Document);
        return (first.Document, SvgReader.Read(svg).Document, svg);
    }

    /// <summary>
    /// Compares two dumps line for line, with one tolerance: a number that differs only in the last bits of a
    /// double, which is what recomposing a transform costs. A colour is already quantised by the dump, so a colour
    /// difference of one step in 255 is a real difference and not excused.
    /// </summary>
    private static void AssertSameDump(List<string> expected, List<string> actual, string? what = null)
    {
        string where = what is null ? string.Empty : $" in {what}";

        Assert.True(expected.Count == actual.Count,
            $"the round trip changed the number of model lines{where}: {expected.Count} -> {actual.Count}\n" +
            FirstDifference(expected, actual));

        for (int i = 0; i < expected.Count; i++)
        {
            if (!Same(expected[i], actual[i]))
            {
                Assert.Fail($"the round trip changed the model{where}:\n  was: {expected[i]}\n  now: {actual[i]}");
            }
        }
    }

    private static string FirstDifference(List<string> expected, List<string> actual)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (!Same(expected[i], actual[i]))
            {
                return $"first difference at line {i}:\n  was: {expected[i]}\n  now: {actual[i]}";
            }
        }

        return "(the shared lines are identical)";
    }

    private static bool Same(string expected, string actual)
    {
        if (expected == actual)
        {
            return true;
        }

        string[] left = Tokens(expected);
        string[] right = Tokens(actual);
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i])
            {
                continue;
            }

            if (double.TryParse(left[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(right[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                // Relative, because a coordinate and a radius are the same number at different scales. The budget
                // is one part in ten million: the reader normalises a gradient and composes transforms, and that
                // arithmetic costs a few of a double's last bits - the corpus shows 6e-8 on a radius, which is
                // recomposition and not a change. Anything a person could see is orders of magnitude larger.
                double tolerance = Math.Max(1e-9, Math.Abs(a) * 1e-7);
                if (Math.Abs(a - b) <= tolerance)
                {
                    continue;
                }
            }

            return false;
        }

        return true;
    }

    private static string[] Tokens(string line)
        => line.Split(new[] { ' ', ',', '(', ')', '[', ']', '/', ':', ';', '|', '@', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries);
}
